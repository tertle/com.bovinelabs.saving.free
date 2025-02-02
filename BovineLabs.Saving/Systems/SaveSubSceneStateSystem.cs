// <copyright file="SaveSubSceneStateSystem.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using BovineLabs.Core.Extensions;
    using BovineLabs.Core.Internal;
    using BovineLabs.Core.Utility;
    using BovineLabs.Saving.Data;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
#if BL_CORE_EXTENSIONS
    using BovineLabs.Core;
#endif

    /// <summary>
    /// Executes just before SubScenes unload or load to auto save unloading SubScenes
    /// as well as marking loading SubScenes to load data onto them when they're done.
    /// </summary>
#if BL_CORE_EXTENSIONS
    [WorldSystemFilter(Worlds.ServerLocal)]
#else
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.LocalSimulation)]
#endif
    [UpdateInGroup(typeof(BeforeSceneSectionStreamingSystemGroup))]
    public partial struct SaveSubSceneStateSystem : ISystem, ISystemStartStop
    {
        private EntityQuery loadQuery;
        private EntityQuery unloadQuery;

        private SaveProcessor saveProcessor;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            this.loadQuery = new EntityQueryBuilder(Allocator.Temp).WithSceneLoadRequest().Build(ref state);
            this.unloadQuery = new EntityQueryBuilder(Allocator.Temp).WithSceneUnloadRequest().Build(ref state);

            state.AddDependency<WorldSaveState>();

            state.RequireAnyForUpdate(this.loadQuery, this.unloadQuery);
        }

        /// <inheritdoc/>
        public void OnStartRunning(ref SystemState state)
        {
            this.saveProcessor = new SaveBuilder(Allocator.Persistent).SubSceneSaver().Create(ref state);
        }

        /// <inheritdoc/>
        public void OnStopRunning(ref SystemState state)
        {
            this.saveProcessor.Dispose();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency.Complete();

            var worldState = SystemAPI.GetSingleton<WorldSaveState>();

            if (!this.loadQuery.IsEmptyIgnoreFilter)
            {
                var chunks = this.loadQuery.ToArchetypeChunkArray(state.WorldUpdateAllocator);
                var entityHandle = SystemAPI.GetEntityTypeHandle();

                foreach (var chunk in chunks)
                {
                    foreach (var entity in chunk.GetNativeArray(entityHandle))
                    {
                        worldState.LoadSet.Add(entity);
                    }
                }
            }

            if (!this.unloadQuery.IsEmptyIgnoreFilter)
            {
                // To ensure any existing saving is complete
                state.Dependency.Complete();

                // SubScenes are about to be unloaded, save them!
                var chunks = this.loadQuery.ToArchetypeChunkArray(state.WorldUpdateAllocator);
                var entityHandle = SystemAPI.GetEntityTypeHandle();

                foreach (var chunk in chunks)
                {
                    foreach (var entity in chunk.GetNativeArray(entityHandle))
                    {
                        SaveSystem.SaveSubScene(ref state, ref this.saveProcessor, worldState.SubSceneSavedData, entity);
                    }
                }
            }
        }
    }
}
