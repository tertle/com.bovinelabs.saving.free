// <copyright file="SubSceneRecordSaver.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using BovineLabs.Core.Extensions;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Profiling;

    internal unsafe struct SubSceneRecordSaver : ISaver
    {
        private readonly SystemState* system;

        private EntityQuery savableRecordQuery;
        private ComponentTypeHandle<SavableScene> savableSceneHandle;
        private BufferTypeHandle<SubSceneRecord> subSceneRecordHandle;
        private EntityQuery commandBufferQuery;

        public SubSceneRecordSaver(SaveBuilder builder)
        {
            this.Key = TypeManager.GetTypeInfo<SubSceneRecord>().StableTypeHash;
            this.system = builder.SystemPtr;

            // this.bufferSystem = this.system.World.GetExistingSystemManaged<EndInitializationEntityCommandBufferSystem>();
            this.savableRecordQuery = builder.GetQuery(ComponentType.ReadOnly<SubSceneRecord>());

            this.savableSceneHandle = builder.System.GetComponentTypeHandle<SavableScene>(true);
            this.subSceneRecordHandle = builder.System.GetBufferTypeHandle<SubSceneRecord>(true);

            this.commandBufferQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<EndSimulationEntityCommandBufferSystem.Singleton>()
                .Build(ref builder.System);
        }

        /// <inheritdoc/>
        public ulong Key { get; }

        private ref SystemState System => ref *this.system;

        /// <inheritdoc/>
        public (Serializer Serializer, JobHandle Dependency) Serialize(NativeList<ArchetypeChunk> chunks, JobHandle dependency)
        {
            this.savableSceneHandle.Update(ref this.System);
            this.subSceneRecordHandle.Update(ref this.System);

            var recordChunks = this.savableRecordQuery.ToArchetypeChunkListAsync(this.System.WorldUpdateAllocator, out var recordDependency);
            dependency = JobHandle.CombineDependencies(dependency, recordDependency);

            var serializer = new Serializer(0, this.System.WorldUpdateAllocator);
            dependency = new SerializeJob
                {
                    RecordChunks = recordChunks.AsDeferredJobArray(),
                    Chunks = chunks.AsDeferredJobArray(),
                    SubSceneRecordType = this.subSceneRecordHandle,
                    SavableSceneType = this.savableSceneHandle,
                    Serializer = serializer,
                    Key = this.Key,
                }
                .Schedule(dependency);

            return (serializer, dependency);
        }

        public JobHandle Deserialize(Deserializer deserializer, EntityMap entityMap, JobHandle dependency)
        {
            var commandBufferSystem = this.commandBufferQuery.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>();
            var commandBuffer = commandBufferSystem.CreateCommandBuffer(this.System.WorldUnmanaged);

            dependency = new DeserializeJob
                {
                    Deserializer = deserializer,
                    EntityMap = entityMap,
                    CommandBuffer = commandBuffer,
                }
                .Schedule(dependency);

            return dependency;
        }

        [BurstCompile]
        private struct SerializeJob : IJob
        {
            [ReadOnly]
            public NativeArray<ArchetypeChunk> RecordChunks;

            [ReadOnly]
            public NativeArray<ArchetypeChunk> Chunks;

            [ReadOnly]
            public BufferTypeHandle<SubSceneRecord> SubSceneRecordType;

            [ReadOnly]
            public ComponentTypeHandle<SavableScene> SavableSceneType;

            public Serializer Serializer;

            public ulong Key;

            public void Execute()
            {
                NativeParallelHashSet<SavableScene> existingRecords;
                using (new ProfilerMarker("CreateExistingRecords").Auto())
                {
                    existingRecords = this.CreateExistingRecords();
                }

                using (new ProfilerMarker("EnsureCapacity").Auto())
                {
                    this.EnsureCapacity();
                }

                using (new ProfilerMarker("Serialize").Auto())
                {
                    this.Serialize(existingRecords);
                }
            }

            private NativeParallelHashSet<SavableScene> CreateExistingRecords()
            {
                var setSize = 0;
                foreach (var chunk in this.Chunks)
                {
                    if (chunk.Has(ref this.SavableSceneType))
                    {
                        setSize += chunk.GetNativeArray(ref this.SavableSceneType).Length;
                    }
                }

                var existingRecords = new NativeParallelHashSet<SavableScene>(setSize, Allocator.Temp);

                foreach (var chunk in this.Chunks)
                {
                    var savableScenes = chunk.GetNativeArray(ref this.SavableSceneType);
                    if (savableScenes.Length > 0)
                    {
                        existingRecords.AddBatchUnsafe(savableScenes);
                    }
                }

                return existingRecords;
            }

            private void EnsureCapacity()
            {
                var capacity = UnsafeUtility.SizeOf<HeaderSaver>() + UnsafeUtility.SizeOf<HeaderRecord>();
                foreach (var chunk in this.RecordChunks)
                {
                    var recordAccessor = chunk.GetBufferAccessor(ref this.SubSceneRecordType);

                    for (var i = 0; i < recordAccessor.Length; i++)
                    {
                        capacity += recordAccessor[i].Length * UnsafeUtility.SizeOf<SavableScene>();
                    }
                }

                this.Serializer.EnsureExtraCapacity(capacity);
            }

            private void Serialize(NativeParallelHashSet<SavableScene> existingRecords)
            {
                var saveIdx = this.Serializer.AllocateNoResize<HeaderSaver>();
                var recordIdx = this.Serializer.AllocateNoResize<HeaderRecord>();

                var count = 0;

                foreach (var chunk in this.RecordChunks)
                {
                    var recordAccessor = chunk.GetBufferAccessor(ref this.SubSceneRecordType);

                    for (var i = 0; i < recordAccessor.Length; i++)
                    {
                        var records = recordAccessor[i];

                        for (var index = 0; index < records.Length; index++)
                        {
                            var record = records[index];
                            if (existingRecords.Contains(record.Savable))
                            {
                                // Still exists continue
                                continue;
                            }

                            // Has been destroyed need to record this
                            count++;
                            this.Serializer.AddNoResize(record.Savable);
                        }
                    }
                }

                var headerSave = this.Serializer.GetAllocation<HeaderSaver>(saveIdx);
                *headerSave = new HeaderSaver
                {
                    Key = this.Key,
                    LengthInBytes = this.Serializer.Data.Length,
                };

                var headerSavable = this.Serializer.GetAllocation<HeaderRecord>(recordIdx);
                *headerSavable = new HeaderRecord
                {
                    Count = count,
                };
            }
        }

        [BurstCompile]
        private struct DeserializeJob : IJob
        {
            [ReadOnly]
            public Deserializer Deserializer;

            [ReadOnly]
            public EntityMap EntityMap;

            public EntityCommandBuffer CommandBuffer;

            public void Execute()
            {
                this.Deserializer.Offset<HeaderSaver>();
                var header = this.Deserializer.Read<HeaderRecord>();

                for (var i = 0; i < header.Count; i++)
                {
                    var destroyedEntity = this.Deserializer.Read<SavableScene>();

                    if (!this.EntityMap.TryGetEntity(destroyedEntity, out var entity))
                    {
                        // Has been removed
                        continue;
                    }

                    this.CommandBuffer.DestroyEntity(entity);
                }
            }
        }

        private struct HeaderRecord
        {
            public int Count;

            // Reserved for future
            public fixed byte Padding[4];
        }
    }
}
