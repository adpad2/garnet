﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Garnet.common;
using Garnet.networking;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Garnet.server
{
    using MainStoreAllocator = SpanByteAllocator<StoreFunctions<SpanByte, SpanByte, SpanByteComparer, SpanByteRecordDisposer>>;
    using MainStoreFunctions = StoreFunctions<SpanByte, SpanByte, SpanByteComparer, SpanByteRecordDisposer>;

    using ObjectStoreAllocator = GenericAllocator<byte[], IGarnetObject, StoreFunctions<byte[], IGarnetObject, ByteArrayKeyComparer, DefaultRecordDisposer<byte[], IGarnetObject>>>;
    using ObjectStoreFunctions = StoreFunctions<byte[], IGarnetObject, ByteArrayKeyComparer, DefaultRecordDisposer<byte[], IGarnetObject>>;

    /// <summary>
    /// Wrapper for store and store-specific information
    /// </summary>
    public sealed unsafe partial class AofProcessor
    {
        readonly StoreWrapper storeWrapper;
        readonly RespServerSession respServerSession;

        private readonly RawStringInput storeInput;
        private readonly ObjectInput objectStoreInput;
        private readonly CustomProcedureInput customProcInput;
        private readonly SessionParseState parseState;

        /// <summary>
        /// Replication offset
        /// </summary>
        internal long ReplicationOffset { get; private set; }

        /// <summary>
        /// Session for main store
        /// </summary>
        readonly BasicContext<SpanByte, SpanByte, RawStringInput, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator> basicContext;

        /// <summary>
        /// Session for object store
        /// </summary>
        readonly BasicContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator> objectStoreBasicContext;

        readonly Dictionary<int, List<byte[]>> inflightTxns;
        readonly byte[] buffer;
        readonly GCHandle handle;
        readonly byte* bufferPtr;

        readonly ILogger logger;
        readonly bool recordToAof;

        /// <summary>
        /// Create new AOF processor
        /// </summary>
        public AofProcessor(
            StoreWrapper storeWrapper,
            bool recordToAof = false,
            ILogger logger = null)
        {
            this.storeWrapper = storeWrapper;
            this.recordToAof = recordToAof;

            ReplicationOffset = 0;

            var replayAofStoreWrapper = new StoreWrapper(
                storeWrapper.version,
                storeWrapper.redisProtocolVersion,
                null,
                storeWrapper.store,
                storeWrapper.objectStore,
                storeWrapper.objectStoreSizeTracker,
                storeWrapper.customCommandManager,
                recordToAof ? storeWrapper.appendOnlyFile : null,
                storeWrapper.serverOptions,
                storeWrapper.subscribeBroker,
                accessControlList: storeWrapper.accessControlList,
                loggerFactory: storeWrapper.loggerFactory);

            this.respServerSession = new RespServerSession(0, networkSender: null, storeWrapper: replayAofStoreWrapper, subscribeBroker: null, authenticator: null, enableScripts: false);

            var session = respServerSession.storageSession.basicContext.Session;
            basicContext = session.BasicContext;
            var objectStoreSession = respServerSession.storageSession.objectStoreBasicContext.Session;
            if (objectStoreSession is not null)
                objectStoreBasicContext = objectStoreSession.BasicContext;

            parseState.Initialize();
            storeInput.parseState = parseState;
            objectStoreInput.parseState = parseState;
            customProcInput.parseState = parseState;

            inflightTxns = new Dictionary<int, List<byte[]>>();
            buffer = new byte[BufferSizeUtils.ServerBufferSize(new MaxSizeSettings())];
            handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            bufferPtr = (byte*)handle.AddrOfPinnedObject();
            this.logger = logger;
        }

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
            basicContext.Session?.Dispose();
            objectStoreBasicContext.Session?.Dispose();
            handle.Free();
        }

        /// <summary>
        /// Recover store using AOF
        /// </summary>
        public unsafe void Recover(long untilAddress = -1)
        {
            logger?.LogInformation("Begin AOF recovery");
            RecoverReplay(untilAddress);
        }

        MemoryResult<byte> output = default;
        private unsafe void RecoverReplay(long untilAddress)
        {
            logger?.LogInformation("Begin AOF replay");
            try
            {
                int count = 0;
                if (untilAddress == -1) untilAddress = storeWrapper.appendOnlyFile.TailAddress;
                using var scan = storeWrapper.appendOnlyFile.Scan(storeWrapper.appendOnlyFile.BeginAddress, untilAddress);

                while (scan.GetNext(MemoryPool<byte>.Shared, out var entry, out var length, out _, out long nextAofAddress))
                {
                    count++;
                    ProcessAofRecord(entry, length);
                    if (count % 100_000 == 0)
                        logger?.LogInformation("Completed AOF replay of {count} records, until AOF address {nextAofAddress}", count, nextAofAddress);
                }

                // Update ReplicationOffset
                ReplicationOffset = untilAddress;

                logger?.LogInformation("Completed full AOF log replay of {count} records", count);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "An error occurred AofProcessor.RecoverReplay");

                if (storeWrapper.serverOptions.FailOnRecoveryError)
                    throw;
            }
            finally
            {
                output.MemoryOwner?.Dispose();
                respServerSession.Dispose();
            }
        }

        internal unsafe void ProcessAofRecord(IMemoryOwner<byte> entry, int length, bool asReplica = false)
        {
            fixed (byte* ptr = entry.Memory.Span)
            {
                ProcessAofRecordInternal(ptr, length, asReplica);
            }
            entry.Dispose();
        }

        /// <summary>
        /// Process AOF record
        /// </summary>
        public unsafe void ProcessAofRecordInternal(byte* ptr, int length, bool asReplica = false)
        {
            AofHeader header = *(AofHeader*)ptr;

            if (inflightTxns.ContainsKey(header.sessionID))
            {
                switch (header.opType)
                {
                    case AofEntryType.TxnAbort:
                        inflightTxns[header.sessionID].Clear();
                        inflightTxns.Remove(header.sessionID);
                        break;
                    case AofEntryType.TxnCommit:
                        ProcessTxn(inflightTxns[header.sessionID]);
                        inflightTxns[header.sessionID].Clear();
                        inflightTxns.Remove(header.sessionID);
                        break;
                    case AofEntryType.StoredProcedure:
                        throw new GarnetException($"Unexpected AOF header operation type {header.opType} within transaction");
                    default:
                        inflightTxns[header.sessionID].Add(new ReadOnlySpan<byte>(ptr, length).ToArray());
                        break;
                }
                return;
            }

            switch (header.opType)
            {
                case AofEntryType.TxnStart:
                    inflightTxns[header.sessionID] = [];
                    break;
                case AofEntryType.TxnAbort:
                case AofEntryType.TxnCommit:
                    // We encountered a transaction end without start - this could happen because we truncated the AOF
                    // after a checkpoint, and the transaction belonged to the previous version. It can safely
                    // be ignored.
                    break;
                case AofEntryType.MainStoreCheckpointStartCommit:
                    if (asReplica && header.storeVersion > storeWrapper.store.CurrentVersion)
                        _ = storeWrapper.TakeCheckpoint(false, StoreType.Main, logger);
                    break;
                case AofEntryType.ObjectStoreCheckpointStartCommit:
                    if (asReplica && header.storeVersion > storeWrapper.objectStore.CurrentVersion)
                        _ = storeWrapper.TakeCheckpoint(false, StoreType.Object, logger);
                    break;
                case AofEntryType.MainStoreCheckpointEndCommit:
                case AofEntryType.ObjectStoreCheckpointEndCommit:
                    break;
                case AofEntryType.MainStoreStreamingCheckpointStartCommit:
                    Debug.Assert(storeWrapper.serverOptions.ReplicaDisklessSync);
                    if (asReplica && header.storeVersion > storeWrapper.store.CurrentVersion)
                        storeWrapper.store.SetVersion(header.storeVersion);
                    break;
                case AofEntryType.MainStoreStreamingCheckpointEndCommit:
                    Debug.Assert(storeWrapper.serverOptions.ReplicaDisklessSync);
                    break;
                case AofEntryType.ObjectStoreStreamingCheckpointStartCommit:
                    Debug.Assert(storeWrapper.serverOptions.ReplicaDisklessSync);
                    if (asReplica && header.storeVersion > storeWrapper.store.CurrentVersion)
                        storeWrapper.objectStore.SetVersion(header.storeVersion);
                    break;
                case AofEntryType.ObjectStoreStreamingCheckpointEndCommit:
                    Debug.Assert(storeWrapper.serverOptions.ReplicaDisklessSync);
                    break;
                default:
                    ReplayOp(ptr);
                    break;
            }
        }

        /// <summary>
        /// Method to process a batch of entries as a single txn.
        /// Assumes that operations arg does not contain transaction markers (i.e. TxnStart,TxnCommit,TxnAbort)
        /// </summary>
        /// <param name="operations"></param>
        private unsafe void ProcessTxn(List<byte[]> operations)
        {
            foreach (byte[] entry in operations)
            {
                fixed (byte* ptr = entry)
                    ReplayOp(ptr);
            }
        }

        private unsafe bool ReplayOp(byte* entryPtr)
        {
            AofHeader header = *(AofHeader*)entryPtr;

            // Skips versions that were part of checkpoint
            if (SkipRecord(header)) return false;

            switch (header.opType)
            {
                case AofEntryType.StoreUpsert:
                    StoreUpsert(basicContext, storeInput, entryPtr);
                    break;
                case AofEntryType.StoreRMW:
                    StoreRMW(basicContext, storeInput, entryPtr);
                    break;
                case AofEntryType.StoreDelete:
                    StoreDelete(basicContext, entryPtr);
                    break;
                case AofEntryType.ObjectStoreRMW:
                    ObjectStoreRMW(objectStoreBasicContext, objectStoreInput, entryPtr, bufferPtr, buffer.Length);
                    break;
                case AofEntryType.ObjectStoreUpsert:
                    ObjectStoreUpsert(objectStoreBasicContext, storeWrapper.GarnetObjectSerializer, entryPtr, bufferPtr, buffer.Length);
                    break;
                case AofEntryType.ObjectStoreDelete:
                    ObjectStoreDelete(objectStoreBasicContext, entryPtr);
                    break;
                case AofEntryType.StoredProcedure:
                    RunStoredProc(header.procedureId, customProcInput, entryPtr);
                    break;
                default:
                    throw new GarnetException($"Unknown AOF header operation type {header.opType}");
            }
            return true;
        }

        unsafe void RunStoredProc(byte id, CustomProcedureInput customProcInput, byte* ptr)
        {
            var curr = ptr + sizeof(AofHeader);

            // Reconstructing CustomProcedureInput

            // input
            customProcInput.DeserializeFrom(curr);

            respServerSession.RunTransactionProc(id, ref customProcInput, ref output);
        }

        static unsafe void StoreUpsert(BasicContext<SpanByte, SpanByte, RawStringInput, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator> basicContext,
            RawStringInput storeInput, byte* ptr)
        {
            var curr = ptr + sizeof(AofHeader);
            ref var key = ref Unsafe.AsRef<SpanByte>(curr);
            curr += key.TotalSize;

            ref var value = ref Unsafe.AsRef<SpanByte>(curr);
            curr += value.TotalSize;

            // Reconstructing RawStringInput

            // input
            storeInput.DeserializeFrom(curr);

            SpanByteAndMemory output = default;
            basicContext.Upsert(ref key, ref storeInput, ref value, ref output);
            if (!output.IsSpanByte)
                output.Memory.Dispose();
        }

        static unsafe void StoreRMW(BasicContext<SpanByte, SpanByte, RawStringInput, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator> basicContext, RawStringInput storeInput, byte* ptr)
        {
            var curr = ptr + sizeof(AofHeader);
            ref var key = ref Unsafe.AsRef<SpanByte>(curr);
            curr += key.TotalSize;

            // Reconstructing RawStringInput

            // input
            storeInput.DeserializeFrom(curr);

            var pbOutput = stackalloc byte[32];
            var output = new SpanByteAndMemory(pbOutput, 32);

            if (basicContext.RMW(ref key, ref storeInput, ref output).IsPending)
                basicContext.CompletePending(true);
            if (!output.IsSpanByte)
                output.Memory.Dispose();
        }

        static unsafe void StoreDelete(BasicContext<SpanByte, SpanByte, RawStringInput, SpanByteAndMemory, long, MainSessionFunctions, MainStoreFunctions, MainStoreAllocator> basicContext, byte* ptr)
        {
            ref var key = ref Unsafe.AsRef<SpanByte>(ptr + sizeof(AofHeader));
            basicContext.Delete(ref key);
        }

        static unsafe void ObjectStoreUpsert(BasicContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator> basicContext,
                GarnetObjectSerializer garnetObjectSerializer, byte* ptr, byte* outputPtr, int outputLength)
        {
            ref var key = ref Unsafe.AsRef<SpanByte>(ptr + sizeof(AofHeader));
            var keyB = key.ToByteArray();

            ref var value = ref Unsafe.AsRef<SpanByte>(ptr + sizeof(AofHeader) + key.TotalSize);
            var valB = garnetObjectSerializer.Deserialize(value.ToByteArray());

            var output = new GarnetObjectStoreOutput { SpanByteAndMemory = new(outputPtr, outputLength) };
            basicContext.Upsert(ref keyB, ref valB);
            if (!output.SpanByteAndMemory.IsSpanByte)
                output.SpanByteAndMemory.Memory.Dispose();
        }

        static unsafe void ObjectStoreRMW(BasicContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator> basicContext,
            ObjectInput objectStoreInput, byte* ptr, byte* outputPtr, int outputLength)
        {
            var curr = ptr + sizeof(AofHeader);
            ref var key = ref Unsafe.AsRef<SpanByte>(curr);
            curr += key.TotalSize;
            var keyB = key.ToByteArray();

            // Reconstructing ObjectInput

            // input
            objectStoreInput.DeserializeFrom(curr);

            // Call RMW with the reconstructed key & ObjectInput
            var output = new GarnetObjectStoreOutput { SpanByteAndMemory = new(outputPtr, outputLength) };
            if (basicContext.RMW(ref keyB, ref objectStoreInput, ref output).IsPending)
                basicContext.CompletePending(true);

            if (!output.SpanByteAndMemory.IsSpanByte)
                output.SpanByteAndMemory.Memory.Dispose();
        }

        static unsafe void ObjectStoreDelete(BasicContext<byte[], IGarnetObject, ObjectInput, GarnetObjectStoreOutput, long, ObjectSessionFunctions, ObjectStoreFunctions, ObjectStoreAllocator> basicContext, byte* ptr)
        {
            ref var key = ref Unsafe.AsRef<SpanByte>(ptr + sizeof(AofHeader));
            var keyB = key.ToByteArray();
            basicContext.Delete(ref keyB);
        }

        /// <summary>
        /// On recovery apply records with header.version greater than CurrentVersion.
        /// </summary>
        /// <param name="header"></param>
        /// <returns></returns>
        /// <exception cref="GarnetException"></exception>
        bool SkipRecord(AofHeader header)
        {
            AofStoreType storeType = ToAofStoreType(header.opType);

            return storeType switch
            {
                AofStoreType.MainStoreType => header.storeVersion <= storeWrapper.store.CurrentVersion - 1,
                AofStoreType.ObjectStoreType => header.storeVersion <= storeWrapper.objectStore.CurrentVersion - 1,
                AofStoreType.TxnType => false,
                AofStoreType.ReplicationType => false,
                AofStoreType.CheckpointType => false,
                _ => throw new GarnetException($"Unknown AOF header store type {storeType}"),
            };
        }

        static AofStoreType ToAofStoreType(AofEntryType type)
        {
            return type switch
            {
                AofEntryType.StoreUpsert or AofEntryType.StoreRMW or AofEntryType.StoreDelete => AofStoreType.MainStoreType,
                AofEntryType.ObjectStoreUpsert or AofEntryType.ObjectStoreRMW or AofEntryType.ObjectStoreDelete => AofStoreType.ObjectStoreType,
                AofEntryType.TxnStart or AofEntryType.TxnCommit or AofEntryType.TxnAbort or AofEntryType.StoredProcedure => AofStoreType.TxnType,
                AofEntryType.MainStoreCheckpointStartCommit or AofEntryType.ObjectStoreCheckpointStartCommit or AofEntryType.MainStoreStreamingCheckpointStartCommit or AofEntryType.ObjectStoreStreamingCheckpointStartCommit => AofStoreType.CheckpointType,
                AofEntryType.MainStoreCheckpointEndCommit or AofEntryType.ObjectStoreCheckpointEndCommit or AofEntryType.MainStoreStreamingCheckpointEndCommit or AofEntryType.ObjectStoreStreamingCheckpointEndCommit => AofStoreType.CheckpointType,
                _ => throw new GarnetException($"Conversion to AofStoreType not possible for {type}"),
            };
        }
    }
}