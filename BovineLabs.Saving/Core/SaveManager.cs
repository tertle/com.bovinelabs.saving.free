// <copyright file="SaveManager.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using System;
    using BovineLabs.Core.Utility;
    using Unity.Burst;
    using Unity.Burst.Intrinsics;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Profiling;
    using Unity.Scenes;
    using UnityEngine;
    using Hash128 = Unity.Entities.Hash128;

    public unsafe struct SaveManager : IDisposable
    {
        private readonly SystemState* systemState;
        private readonly EntityQuery subSceneQuery;

        private SaveProcessor saveProcessor;
        private SaveProcessor subSceneProcessor;
        private SubSceneUtility subSceneUtility;

        private EntityTypeHandle entityTypeHandle;

        private NativeHashMap<SectionIdentifier, NativeList<byte>> subSceneSavedData;
        private NativeList<byte> worldSaveData;

        // The current set of opening and loading subScenes
        private NativeHashSet<Entity> openLoadSet;

        public SaveManager(ref SystemState systemState, SaveBuilder saveBuilder, Allocator allocator = Allocator.Persistent)
        {
            fixed (SystemState* state = &systemState)
            {
                this.systemState = state;
            }

            this.saveProcessor = saveBuilder.Create();
            this.subSceneProcessor = saveBuilder.Clone().SubSceneSaver().Create();

            this.subSceneQuery = new EntityQueryBuilder(Allocator.Temp).WithAll<SceneReference, ResolvedSectionEntity>().Build(ref systemState);
            this.entityTypeHandle = systemState.GetEntityTypeHandle();

            this.subSceneUtility = new SubSceneUtility(ref systemState);
            this.subSceneSavedData = new NativeHashMap<SectionIdentifier, NativeList<byte>>(0, allocator);
            this.worldSaveData = new NativeList<byte>(0, Allocator.Persistent);
            this.openLoadSet = new NativeHashSet<Entity>(0, Allocator.Persistent);
        }

        private ref SystemState SystemState => ref *this.systemState;

        /// <inheritdoc/>
        public void Dispose()
        {
            this.saveProcessor.Dispose();
            this.subSceneProcessor.Dispose();

            foreach (var save in this.subSceneSavedData)
            {
                var list = save.Value;
                list.Dispose();
            }

            this.subSceneSavedData.Dispose();
            this.worldSaveData.Dispose();
            this.openLoadSet.Dispose();
        }

        public void ResetFilter()
        {
            this.saveProcessor.ResetFilter();
            this.subSceneProcessor.ResetFilter();
        }

        public void SetSharedComponentFilter<T>(T sharedComponent)
            where T : unmanaged, ISharedComponentData
        {
            this.saveProcessor.SetSharedComponentFilter(sharedComponent);
            this.subSceneProcessor.SetSharedComponentFilter(sharedComponent);
        }

        public JobHandle Save(ref NativeList<byte> savedData, JobHandle dependency)
        {
            using var marker = new ProfilerMarker("Save").Auto();

            dependency = this.HandleSave(dependency);
            dependency = this.WriteSaveData(ref savedData, dependency);
            return dependency;
        }

        public JobHandle Load(NativeArray<byte> saveData, JobHandle dependency)
        {
            using var marker = new ProfilerMarker("Load").Auto();

            var deserializer = new Deserializer(saveData, 0);

            using var e = this.subSceneSavedData.GetEnumerator();
            while (e.MoveNext())
            {
                e.Current.Value.Clear();
            }

            this.worldSaveData.Clear();

            while (!deserializer.IsAtEnd)
            {
                var header = deserializer.Read<Header>();

                NativeList<byte> sceneData;

                if (header.Key.SceneGuid == default)
                {
                    sceneData = this.worldSaveData;
                }
                else if (!this.subSceneSavedData.TryGetValue(header.Key, out sceneData))
                {
                    sceneData = this.subSceneSavedData[header.Key] = new NativeList<byte>(header.LengthInBytes, Allocator.Persistent);
                }

                if (sceneData.Capacity < header.LengthInBytes)
                {
                    sceneData.Capacity = header.LengthInBytes;
                }

                var data = deserializer.ReadBuffer<byte>(header.LengthInBytes);
                sceneData.AddRange(data, header.LengthInBytes);
            }

            var openSubScenes = new NativeList<Entity>(this.SystemState.WorldUpdateAllocator);
            this.subSceneUtility.Update(ref this.SystemState);
            this.entityTypeHandle.Update(ref this.SystemState);

            new GetAllOpenSubScenesJob { EntityHandle = this.entityTypeHandle, SubSceneUtility = this.subSceneUtility, OpenSubScenes = openSubScenes }
                .Run(this.subSceneQuery);

            if (this.worldSaveData.Length > 0)
            {
                dependency = this.saveProcessor.Load(this.worldSaveData.AsArray(), dependency);
            }

            foreach (var subScene in openSubScenes)
            {
                var sections = this.SystemState.EntityManager.GetBuffer<ResolvedSectionEntity>(subScene);
                foreach (var section in sections.AsNativeArray())
                {
                    var sceneSectionData = this.SystemState.EntityManager.GetComponentData<SceneSectionData>(section.SectionEntity);
                    var key = new SectionIdentifier(sceneSectionData.SceneGUID, sceneSectionData.SubSectionIndex);

                    if (!this.subSceneSavedData.TryGetValue(key, out var data))
                    {
                        continue;
                    }

                    if (data.Length == 0)
                    {
                        continue;
                    }

                    var sceneSection = new SceneSection { SceneGUID = sceneSectionData.SceneGUID, Section = sceneSectionData.SubSectionIndex };
                    this.subSceneProcessor.SetSubSceneFilter(sceneSection);
                    dependency = this.subSceneProcessor.Load(data.AsArray(), dependency);
                }
            }

            return dependency;
        }

        public JobHandle SaveClose(Entity subScene, JobHandle dependency)
        {
            var subScenes = CollectionHelper.CreateNativeArray<Entity>(1, this.SystemState.WorldUpdateAllocator);
            subScenes[0] = subScene;
            return this.SaveClose(subScenes, dependency);
        }

        public JobHandle SaveClose(NativeArray<Entity> subScenes, JobHandle dependency)
        {
            foreach (var entity in subScenes)
            {
                if (!SubSceneUtility.IsSceneLoaded(ref this.SystemState, entity))
                {
                    Debug.LogWarning("Save & Close request on SubScene that isn't open.");
                    continue;
                }

                SubSceneUtility.CloseScene(ref this.SystemState, entity);
            }

            foreach (var entity in subScenes)
            {
                // TODO can these be done in parallel?
                dependency = this.SaveSubScene(entity, dependency);
            }

            return dependency;
        }

        public void OpenLoad(Entity subScene)
        {
            var subScenes = CollectionHelper.CreateNativeArray<Entity>(1, this.SystemState.WorldUpdateAllocator);
            subScenes[0] = subScene;
            this.OpenLoad(subScenes);
        }

        public void OpenLoad(NativeArray<Entity> subScenes)
        {
            foreach (var entity in subScenes)
            {
                if (SubSceneUtility.IsSceneLoaded(ref this.SystemState, entity))
                {
                    Debug.LogWarning("Open & Load request on SubScene that is already open.");
                    return;
                }

                // Already have a request
                if (this.openLoadSet.Contains(entity))
                {
                    return;
                }

                this.openLoadSet.Add(entity);
                SubSceneUtility.OpenScene(ref this.SystemState, entity);
            }
        }

        public JobHandle Update(JobHandle dependency)
        {
            if (this.openLoadSet.IsEmpty)
            {
                return dependency;
            }

            var openLoad = this.openLoadSet;
            this.subSceneUtility.Update(ref this.SystemState);
            var subSceneUtil = this.subSceneUtility;

            // Clone so we can remove
            foreach (var entity in openLoad.ToNativeArray(Allocator.Temp))
            {
                if (!subSceneUtil.IsSceneLoaded(entity))
                {
                    continue;
                }

                openLoad.Remove(entity);

                var sections = this.SystemState.EntityManager.GetBuffer<ResolvedSectionEntity>(entity).AsNativeArray();
                foreach (var section in sections)
                {
                    var sceneSectionData = this.SystemState.EntityManager.GetComponentData<SceneSectionData>(section.SectionEntity);
                    var key = new SectionIdentifier(sceneSectionData.SceneGUID, sceneSectionData.SubSectionIndex);

                    if (!this.subSceneSavedData.TryGetValue(key, out var saveData))
                    {
                        continue;
                    }

                    this.subSceneProcessor.SetSubSceneFilter(new SceneSection
                    {
                        SceneGUID = sceneSectionData.SceneGUID,
                        Section = sceneSectionData.SubSectionIndex,
                    });

                    dependency = this.subSceneProcessor.Load(saveData.AsArray(), dependency);
                }
            }

            return dependency;
        }

        private JobHandle HandleSave(JobHandle dependency)
        {
            var openSubScenes = new NativeList<Entity>(this.SystemState.WorldUpdateAllocator);
            this.subSceneUtility.Update(ref this.SystemState);
            this.entityTypeHandle.Update(ref this.SystemState);

            new GetAllOpenSubScenesJob { EntityHandle = this.entityTypeHandle, SubSceneUtility = this.subSceneUtility, OpenSubScenes = openSubScenes }
                .Run(this.subSceneQuery);

            // Save all sub scenes
            foreach (var subScene in openSubScenes)
            {
                // TODO can these be done in parallel?
                dependency = this.SaveSubScene(subScene, dependency);
            }

            // Save the rest of the world
            dependency = this.saveProcessor.Save(ref this.worldSaveData, dependency);
            return dependency;
        }

        private JobHandle WriteSaveData(ref NativeList<byte> saveData, JobHandle dependency)
        {
            var keyIndexMap = new NativeHashMap<SectionIdentifier, int>(
                this.subSceneSavedData.Count + 1, this.SystemState.WorldUpdateAllocator);
            var startIndex = new NativeReference<int>(this.SystemState.WorldUpdateAllocator);

            dependency = new CalculateKeyIndexMapJob
                {
                    KeyIndexMap = keyIndexMap,
                    Key = default,
                    SaveData = this.worldSaveData,
                    StartIndex = startIndex,
                }
                .Schedule(dependency);

            foreach (var subScene in this.subSceneSavedData)
            {
                dependency = new CalculateKeyIndexMapJob
                    {
                        KeyIndexMap = keyIndexMap,
                        Key = subScene.Key,
                        SaveData = subScene.Value,
                        StartIndex = startIndex,
                    }
                    .Schedule(dependency);
            }

            dependency = new ResizeOutputJob
                {
                    Length = startIndex,
                    SaveData = saveData,
                }
                .Schedule(dependency);

            var resizeDependency = dependency;
            var handles = new NativeArray<JobHandle>(this.subSceneSavedData.Count + 1, Allocator.Temp);

            var index = 0;

            handles[index++] = new MergeSaveDataJob
                {
                    SaveData = saveData,
                    Input = this.worldSaveData,
                    KeyIndexMap = keyIndexMap,
                    Key = default,
                }
                .Schedule(resizeDependency);

            foreach (var subScene in this.subSceneSavedData)
            {
                handles[index++] = new MergeSaveDataJob
                    {
                        SaveData = saveData,
                        Input = subScene.Value,
                        KeyIndexMap = keyIndexMap,
                        Key = new SectionIdentifier(subScene.Key.SceneGuid, subScene.Key.SubSectionIndex),
                    }
                    .Schedule(resizeDependency);
            }

            dependency = JobHandle.CombineDependencies(handles);
            return dependency;
        }

        /// <summary> Starts the process of saving a sub scene. </summary>
        private JobHandle SaveSubScene(Entity subScene, JobHandle dependency)
        {
            var sections = this.SystemState.EntityManager.GetBuffer<ResolvedSectionEntity>(subScene);

            foreach (var section in sections.AsNativeArray())
            {
                var sceneSectionData = this.SystemState.EntityManager.GetComponentData<SceneSectionData>(section.SectionEntity);
                var key = new SectionIdentifier(sceneSectionData.SceneGUID, sceneSectionData.SubSectionIndex);

                if (!this.subSceneSavedData.TryGetValue(key, out var saveList))
                {
                    this.subSceneSavedData[key] = saveList = new NativeList<byte>(1024, Allocator.Persistent);
                }

                // Filter to only entities within this sub scene section
                this.subSceneProcessor.SetSubSceneFilter(
                    new SceneSection { SceneGUID = sceneSectionData.SceneGUID, Section = sceneSectionData.SubSectionIndex });
                dependency = this.subSceneProcessor.Save(ref saveList, dependency); // TODO can these be done in parallel?
            }

            return dependency;
        }

        [BurstCompile]
        private struct GetAllOpenSubScenesJob : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle EntityHandle;

            public SubSceneUtility SubSceneUtility;
            public NativeList<Entity> OpenSubScenes;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetEntityDataPtrRO(this.EntityHandle);
                for (var index = 0; index < chunk.Count; index++)
                {
                    var entity = entities[index];
                    if (!this.SubSceneUtility.IsSceneLoaded(entity))
                    {
                        return;
                    }

                    this.OpenSubScenes.Add(entity);
                }
            }
        }

        [BurstCompile]
        private struct CalculateKeyIndexMapJob : IJob
        {
            public NativeHashMap<SectionIdentifier, int> KeyIndexMap;
            public NativeReference<int> StartIndex;

            [ReadOnly]
            public NativeList<byte> SaveData;

            public SectionIdentifier Key;

            public void Execute()
            {
                this.KeyIndexMap.Add(this.Key, this.StartIndex.Value);
                this.StartIndex.Value += this.SaveData.Length + UnsafeUtility.SizeOf<Header>();
            }
        }

        [BurstCompile]
        private struct ResizeOutputJob : IJob
        {
            [ReadOnly]
            public NativeReference<int> Length;

            public NativeList<byte> SaveData;

            public void Execute()
            {
                this.SaveData.ResizeUninitialized(this.Length.Value);
            }
        }

        [BurstCompile]
        private struct MergeSaveDataJob : IJob
        {
            [NoAlias]
            [NativeDisableContainerSafetyRestriction]
            public NativeList<byte> SaveData;

            [ReadOnly]
            public NativeList<byte> Input;

            [ReadOnly]
            public NativeHashMap<SectionIdentifier, int> KeyIndexMap;

            public SectionIdentifier Key;

            public void Execute()
            {
                var index = this.KeyIndexMap[this.Key];
                var start = this.SaveData.GetUnsafePtr() + index;

                var header = new Header { Key = this.Key, LengthInBytes = this.Input.Length };
                UnsafeUtility.MemCpy(start, &header, UnsafeUtility.SizeOf<Header>());
                UnsafeUtility.MemCpy(start + UnsafeUtility.SizeOf<Header>(), this.Input.GetUnsafeReadOnlyPtr(), this.Input.Length);
            }
        }

        private struct Header
        {
            public SectionIdentifier Key;

            // Length of the data, not including the Header
            public int LengthInBytes;

            private fixed byte buffer[8];
        }

        private readonly struct SectionIdentifier : IEquatable<SectionIdentifier>
        {
            public readonly Hash128 SceneGuid;
            public readonly int SubSectionIndex;

            public SectionIdentifier(Hash128 sceneGuid, int subSectionIndex)
            {
                this.SceneGuid = sceneGuid;
                this.SubSectionIndex = subSectionIndex;
            }

            public bool Equals(SectionIdentifier other)
            {
                return this.SceneGuid.Equals(other.SceneGuid) && this.SubSectionIndex == other.SubSectionIndex;
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return this.SceneGuid.GetHashCode() * 397 ^ this.SubSectionIndex;
                }
            }
        }
    }
}
