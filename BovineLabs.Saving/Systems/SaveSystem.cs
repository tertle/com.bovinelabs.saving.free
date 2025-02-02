// <copyright file="SaveSystem.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using BovineLabs.Core;
    using BovineLabs.Core.Utility;
    using BovineLabs.Saving.Data;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using Unity.Jobs;

#if BL_CORE_EXTENSIONS
    [WorldSystemFilter(Worlds.ServerLocal)]
    [UpdateInGroup(typeof(Core.Groups.AfterSceneSystemGroup))]
#else
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(Unity.Scenes.SceneSystemGroup))]
#endif
    public unsafe partial struct SaveSystem : ISystem
    {
        private WorldSaveState saveState;

        private SaveProcessor saveProcessor;
        private SubSceneUtil subSceneUtil;

        private EntityQuery loadQuery;
        private EntityQuery saveQuery;
        private EntityQuery stateQuery;

        /// <summary> Saves a subscene. </summary>
        /// <param name="state"> The systems state. </param>
        /// <param name="saveProcessor"> The save processor belonging to the system. </param>
        /// <param name="subSceneSavedData"> The hashmap that the save data gets written to. </param>
        /// <param name="entity"> The <see cref="SceneSectionData"/> entity. </param>
        public static void SaveSubScene(
            ref SystemState state, ref SaveProcessor saveProcessor, NativeHashMap<SectionIdentifier, NativeList<byte>> subSceneSavedData, Entity entity)
        {
            var sceneSectionData = state.EntityManager.GetComponentData<SceneSectionData>(entity);
            var key = new SectionIdentifier(sceneSectionData.SceneGUID, sceneSectionData.SubSectionIndex);

            if (!subSceneSavedData.TryGetValue(key, out var saveList))
            {
                subSceneSavedData[key] = saveList = new NativeList<byte>(256, Allocator.Persistent);
            }

            // Filter to only entities within this sub scene section
            saveProcessor.SetSubSceneFilter(new SceneSection
            {
                SceneGUID = sceneSectionData.SceneGUID,
                Section = sceneSectionData.SubSectionIndex,
            });

            state.Dependency = saveProcessor.Save(ref state, saveList, state.Dependency); // TODO can these be done in parallel?
        }

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            this.subSceneUtil = new SubSceneUtil(ref state);
            this.saveProcessor = new SaveBuilder(Allocator.Persistent).SubSceneSaver().Create(ref state);

            this.saveState = new WorldSaveState
            {
                SubSceneSavedData = new NativeHashMap<SectionIdentifier, NativeList<byte>>(0, Allocator.Persistent),
                LoadSet = new NativeHashSet<Entity>(0, Allocator.Persistent),
            };

            var entity = state.EntityManager.CreateSingleton<WorldSaveState>();
            state.EntityManager.SetComponentData(entity, this.saveState);

            this.loadQuery = SystemAPI.QueryBuilder().WithAll<LoadBuffer>().Build();
            this.saveQuery = SystemAPI.QueryBuilder().WithAll<SaveRequest>().Build();
            this.stateQuery = SystemAPI.QueryBuilder().WithAll<WorldSaveState>().Build();
        }

        /// <inheritdoc/>
        public void OnDestroy(ref SystemState state)
        {
            this.saveProcessor.Dispose();

            foreach (var save in this.saveState.SubSceneSavedData)
            {
                var list = save.Value;
                list.Dispose();
            }

            this.saveState.SubSceneSavedData.Dispose();
            this.saveState.LoadSet.Dispose();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var debug = SystemAPI.GetSingleton<BLDebug>();

            this.TryLoad(ref state, debug);
            var didApply = this.TryApply(ref state, debug);
            this.TrySave(ref state, didApply, debug);
        }

        private void TryLoad(ref SystemState state, BLDebug debug)
        {
            if (this.loadQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            var loadBuffer = this.loadQuery.GetSingletonBuffer<LoadBuffer>();

            debug.Debug($"Loading save of size {loadBuffer.Length} bytes");

            this.Deserialize(loadBuffer.Reinterpret<byte>());
            state.EntityManager.DestroyEntity(this.loadQuery);
        }

        private bool TryApply(ref SystemState state, BLDebug debug)
        {
            this.stateQuery.CompleteDependency();
            if (this.saveState.LoadSet.IsEmpty)
            {
                return false;
            }

            var any = false;

            foreach (var entity in this.saveState.LoadSet.ToNativeArray(Allocator.Temp))
            {
                if (!SubSceneUtil.IsSectionLoaded(ref state, entity))
                {
                    continue;
                }

                this.saveState.LoadSet.Remove(entity);

                var sceneSectionData = state.EntityManager.GetComponentData<SceneSectionData>(entity);
                var key = new SectionIdentifier(sceneSectionData.SceneGUID, sceneSectionData.SubSectionIndex);

                if (!this.saveState.SubSceneSavedData.TryGetValue(key, out var saveData))
                {
                    continue;
                }

                this.saveProcessor.SetSubSceneFilter(new SceneSection
                {
                    SceneGUID = sceneSectionData.SceneGUID,
                    Section = sceneSectionData.SubSectionIndex,
                });

                debug.Verbose($"Apply save data to {entity.ToFixedString()}");

                state.Dependency = this.saveProcessor.Load(ref state, saveData.AsArray(), state.Dependency);
                any = true;
            }

            return any;
        }

        private void TrySave(ref SystemState state, bool completeDependency, BLDebug debug)
        {
            if (this.saveQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            debug.Debug("Saving the world");

            // On the rare chance we load data same frame as we need to save, we need to finish that work otherwise we'll have a dependency issue
            if (completeDependency)
            {
                state.Dependency.Complete();
            }

            state.EntityManager.DestroyEntity(this.saveQuery);

            var saveEntity = state.EntityManager.CreateEntity();
            var saveBuffer = state.EntityManager.AddBuffer<SaveBuffer>(saveEntity);

            this.Save(ref state, saveBuffer.Reinterpret<byte>());
        }

        private void Deserialize(DynamicBuffer<byte> saveData)
        {
            var deserializer = new Deserializer(saveData.AsNativeArray());

            // Clear any existing data
            using var e = this.saveState.SubSceneSavedData.GetEnumerator();
            while (e.MoveNext())
            {
                e.Current.Value.Clear();
            }

            while (!deserializer.IsAtEnd)
            {
                var header = deserializer.Read<Header>();

                if (!this.saveState.SubSceneSavedData.TryGetValue(header.Key, out var sceneData))
                {
                    sceneData = this.saveState.SubSceneSavedData[header.Key] = new NativeList<byte>(header.LengthInBytes, Allocator.Persistent);
                }

                if (sceneData.Capacity < header.LengthInBytes)
                {
                    sceneData.Capacity = header.LengthInBytes;
                }

                var data = deserializer.ReadBuffer<byte>(header.LengthInBytes);
                sceneData.AddRange(data, header.LengthInBytes);
            }
        }

        private void Save(ref SystemState state, DynamicBuffer<byte> savedData)
        {
            this.SaveAllOpenSubScenes(ref state);
            this.WriteSaveData(ref state, savedData);
        }

        private void SaveAllOpenSubScenes(ref SystemState state)
        {
            var openSubSceneSections = new NativeList<Entity>(state.WorldUpdateAllocator);

            this.subSceneUtil.Update(ref state);

            new GetAllOpenSubScenesJob
            {
                SubSceneUtil = this.subSceneUtil,
                OpenSubSceneSections = openSubSceneSections,
            }.Run();

            // Save all sub scenes
            foreach (var subScene in openSubSceneSections)
            {
                SaveSubScene(ref state, ref this.saveProcessor, this.saveState.SubSceneSavedData, subScene);
            }
        }

        private void WriteSaveData(ref SystemState state, DynamicBuffer<byte> saveData)
        {
            var keyIndexMap = new NativeHashMap<SectionIdentifier, int>(this.saveState.SubSceneSavedData.Count, state.WorldUpdateAllocator);
            var startIndex = new NativeReference<int>(state.WorldUpdateAllocator);

            foreach (var subScene in this.saveState.SubSceneSavedData)
            {
                state.Dependency = new CalculateKeyIndexMapJob
                {
                    KeyIndexMap = keyIndexMap,
                    Key = subScene.Key,
                    SaveData = subScene.Value,
                    StartIndex = startIndex,
                }.Schedule(state.Dependency);
            }

            state.Dependency = new ResizeOutputJob
            {
                Length = startIndex,
                SaveData = saveData,
            }.Schedule(state.Dependency);

            var resizeDependency = state.Dependency;
            var handles = new NativeArray<JobHandle>(this.saveState.SubSceneSavedData.Count, Allocator.Temp);

            var index = 0;

            foreach (var subScene in this.saveState.SubSceneSavedData)
            {
                handles[index++] = new MergeSaveDataJob
                {
                    SaveData = saveData,
                    Input = subScene.Value,
                    KeyIndexMap = keyIndexMap,
                    Key = new SectionIdentifier(subScene.Key.SceneGuid, subScene.Key.SubSectionIndex),
                }.Schedule(resizeDependency);
            }

            state.Dependency = JobHandle.CombineDependencies(handles);
        }

        [BurstCompile]
        [WithAll(typeof(SceneSectionData))]
        private partial struct GetAllOpenSubScenesJob : IJobEntity
        {
            public SubSceneUtil SubSceneUtil;
            public NativeList<Entity> OpenSubSceneSections;

            private void Execute(Entity entity)
            {
                if (!this.SubSceneUtil.IsSectionLoaded(entity))
                {
                    return;
                }

                this.OpenSubSceneSections.Add(entity);
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

            public DynamicBuffer<byte> SaveData;

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
            public DynamicBuffer<byte> SaveData;

            [ReadOnly]
            public NativeList<byte> Input;

            [ReadOnly]
            public NativeHashMap<SectionIdentifier, int> KeyIndexMap;

            public SectionIdentifier Key;

            public void Execute()
            {
                var index = this.KeyIndexMap[this.Key];
                var start = (byte*)this.SaveData.GetUnsafePtr() + index;

                var header = new Header
                {
                    Key = this.Key,
                    LengthInBytes = this.Input.Length,
                };

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
    }
}
