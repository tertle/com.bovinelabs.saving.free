// <copyright file="SavableBakingSystem.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Authoring
{
    using System;
    using BovineLabs.Saving.Data;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using UnityEditor;

    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    public partial struct SavableBakingSystem : ISystem
    {
        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // Destroy any existing records for live baking
            var oldQuery = SystemAPI.QueryBuilder().WithAny<SavablePrefabRecord, SavableSceneRecord>().Build();
            state.EntityManager.DestroyEntity(oldQuery);

            var prefabs = new NativeHashMap<SceneSectionWrapper, NativeList<SavablePrefabRecord>>(8, state.WorldUpdateAllocator);
            var records = new NativeHashMap<SceneSectionWrapper, RecordData>(8, state.WorldUpdateAllocator);
            var ecb = new EntityCommandBuffer(state.WorldUpdateAllocator);

            foreach (var (savable, entity) in SystemAPI.Query<RootSavable>().WithOptions(EntityQueryOptions.IncludePrefab).WithEntityAccess())
            {
                if (savable.IsPrefab)
                {
                    var assetGuid = savable.GlobalObjectId.assetGUID;
                    var prefab = UnsafeUtility.As<GUID, SavablePrefabRecord>(ref assetGuid);

                    // Odd case if entity just dropped in a non-subscene
                    if (state.EntityManager.HasComponent<SceneSection>(entity))
                    {
                        var sceneSection = state.EntityManager.GetSharedComponent<SceneSection>(entity);

                        if (!prefabs.TryGetValue(sceneSection, out var prefabList))
                        {
                            prefabList = prefabs[sceneSection] = new NativeList<SavablePrefabRecord>(16, state.WorldUpdateAllocator);
                        }

                        ecb.AddComponent(entity, new SavablePrefab { Value = (ushort)prefabList.Length });
                        prefabList.Add(prefab);
                    }
                }
                else
                {
                    // Odd case if entity just dropped in a non-subscene
                    if (state.EntityManager.HasComponent<SceneSection>(entity))
                    {
                        var record = new SavableSceneRecord
                        {
                            TargetObjectId = savable.GlobalObjectId.targetObjectId,
                            TargetPrefabId = savable.GlobalObjectId.targetPrefabId,
                        };

                        var sceneSection = state.EntityManager.GetSharedComponent<SceneSection>(entity);
                        if (!records.TryGetValue(sceneSection, out var recordList))
                        {
                            recordList = records[sceneSection] = new RecordData
                            {
                                Records = new NativeList<SavableSceneRecord>(16, state.WorldUpdateAllocator),
                                Entities = new NativeList<Entity>(16, state.WorldUpdateAllocator),
                            };
                        }

                        ecb.AddComponent<SavableScene>(entity);
                        recordList.Records.Add(record);
                        recordList.Entities.Add(entity);
                    }
                }
            }

            ecb.Playback(state.EntityManager);

            ReadOnlySpan<ComponentType> prefabRecordArchetype = stackalloc[]
            {
                ComponentType.ReadWrite<SceneSection>(),
                ComponentType.ReadWrite<Savable>(),
                ComponentType.ReadWrite<SavablePrefabRecord>(),
            };

            using var e = prefabs.GetEnumerator();
            while (e.MoveNext())
            {
                var current = e.Current;

                var sceneSection = (SceneSection)current.Key;
                var prefabEntity = state.EntityManager.CreateEntity(prefabRecordArchetype);
                state.EntityManager.SetSharedComponent(prefabEntity, sceneSection);

                var prefabsBuffer = state.EntityManager.GetBuffer<SavablePrefabRecord>(prefabEntity);
                prefabsBuffer.AddRange(current.Value.AsArray());
            }

            ReadOnlySpan<ComponentType> sceneRecordArchetype = stackalloc[]
            {
                ComponentType.ReadWrite<SceneSection>(),
                ComponentType.ReadWrite<SavableSceneRecord>(),
                ComponentType.ReadWrite<SavableSceneRecordEntity>(),
            };

            // var (sections, length) = records.GetUniqueKeyArray(Allocator.Temp);
            using var r = records.GetEnumerator();
            while (r.MoveNext())
            {
                var current = r.Current;

                var sceneSection = (SceneSection)current.Key;
                var recordEntity = state.EntityManager.CreateEntity(sceneRecordArchetype);
                state.EntityManager.SetSharedComponent(recordEntity, sceneSection);

                var recordBuffer = state.EntityManager.GetBuffer<SavableSceneRecord>(recordEntity);
                recordBuffer.AddRange(current.Value.Records.AsArray());

                var recordEntityBuffer = state.EntityManager.GetBuffer<SavableSceneRecordEntity>(recordEntity).Reinterpret<Entity>();
                recordEntityBuffer.AddRange(current.Value.Entities.AsArray());
            }
        }

        private struct RecordData
        {
            public NativeList<SavableSceneRecord> Records;
            public NativeList<Entity> Entities;
        }

        private struct SceneSectionWrapper : IEquatable<SceneSectionWrapper>, IComparable<SceneSectionWrapper>
        {
            private SceneSection sceneSection;

            public static implicit operator SceneSection(SceneSectionWrapper wrapper)
            {
                return wrapper.sceneSection;
            }

            public static implicit operator SceneSectionWrapper(SceneSection sceneSection)
            {
                return new SceneSectionWrapper { sceneSection = sceneSection };
            }

            public bool Equals(SceneSectionWrapper other)
            {
                return this.sceneSection.Equals(other.sceneSection);
            }

            public int CompareTo(SceneSectionWrapper other)
            {
                // SceneGUID should always be the same within this system
                return this.sceneSection.Section.CompareTo(other.sceneSection.Section);
            }

            public override int GetHashCode()
            {
                return this.sceneSection.GetHashCode();
            }
        }
    }
}
