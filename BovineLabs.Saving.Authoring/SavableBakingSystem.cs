// <copyright file="SavableBakingSystem.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Authoring
{
    using System;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using UnityEditor;

    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    public partial struct SavableBakingSystem : ISystem
    {
        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
        }

        /// <inheritdoc/>
        public void OnDestroy(ref SystemState state)
        {
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var records = new NativeMultiHashMap<SceneSectionWrapper, SavableScene>(8, Allocator.Temp);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            var query = SystemAPI.QueryBuilder().WithAll<RootSavable>().WithOptions(EntityQueryOptions.IncludePrefab).Build();
            var entities = query.ToEntityArray(Allocator.Temp);
            var rootSavables = query.ToComponentDataArray<RootSavable>(Allocator.Temp);
            for (var index = 0; index < rootSavables.Length; index++)
            {
                var entity = entities[index];
                var savable = rootSavables[index];

                // BUG: ArgumentException: Type BovineLabs.Saving.Authoring.SavableBakingSystem couldn't be found in the SystemRegistry.
                // foreach (var (savable, entity) in SystemAPI.Query<RootSavable>().WithEntityQueryOptions(EntityQueryOptions.IncludePrefab).WithEntityAccess())
                {
                    if (savable.IsPrefab)
                    {
                        var assetGuid = savable.GlobalObjectId.assetGUID;
                        var prefab = UnsafeUtility.As<GUID, SavablePrefab>(ref assetGuid);
                        ecb.AddSharedComponent(entity, prefab);
                    }
                    else
                    {
                        var savableScene = new SavableScene
                        {
                            TargetObjectId = savable.GlobalObjectId.targetObjectId,
                            TargetPrefabId = savable.GlobalObjectId.targetPrefabId,
                        };

                        ecb.AddComponent(entity, savableScene);
                        var sceneSection = state.EntityManager.GetSharedComponent<SceneSection>(entity);
                        records.Add(sceneSection, savableScene);
                    }
                }
            }

            ecb.Playback(state.EntityManager);

            var (sections, length) = records.GetUniqueKeyArray(Allocator.Temp);
            for (var index = 0; index < length; index++)
            {
                var sceneSection = (SceneSection)sections[index];
                var recordEntity = state.EntityManager.CreateEntity();
                state.EntityManager.AddSharedComponent(recordEntity, sceneSection);
                state.EntityManager.AddComponent<Savable>(recordEntity);

                var recordBuffer = state.EntityManager.AddBuffer<SubSceneRecord>(recordEntity);

                var values = records.GetValuesForKey(sceneSection);
                while (values.MoveNext())
                {
                    recordBuffer.Add(new SubSceneRecord { Savable = values.Current });
                }
            }
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
