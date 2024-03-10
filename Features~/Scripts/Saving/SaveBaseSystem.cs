// <copyright file="SaveBaseSystem.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Saving
{
    using BovineLabs.Core.Extensions;
    using BovineLabs.Saving.Samples.Common;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Scenes;
    using UnityEngine;

    [UpdateAfter(typeof(SceneSystemGroup))]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public abstract partial class SaveBaseSystem : SystemBase
    {
        private EntityQuery loadQuery;
        private EntityQuery openLoadQuery;

        private EntityArchetype saveBufferArchetype;
        private EntityQuery saveCloseQuery;
        private SaveManager saveManager;

        private EntityQuery saveQuery;

        protected abstract ComponentType SaveType { get; }

        protected virtual SaveBuilder CreateSaveBuilder(SaveBuilder saveBuilder)
        {
            return saveBuilder;
        }

        protected virtual void ManagerCreated(SaveManager manager)
        {
        }

        /// <inheritdoc/>
        protected override void OnCreate()
        {
            var builder = this.CreateSaveBuilder(new SaveBuilder(ref this.CheckedStateRef));
            this.saveManager = new SaveManager(ref this.CheckedStateRef, builder);
            this.ManagerCreated(this.saveManager);

            this.loadQuery = new EntityQueryBuilder(Allocator.Temp).WithAllRW<Load>().WithAll(this.SaveType)
                .WithOptions(EntityQueryOptions.FilterWriteGroup).Build(this);

            this.saveQuery = new EntityQueryBuilder(Allocator.Temp).WithAllRW<Save>().WithAll(this.SaveType)
                .WithOptions(EntityQueryOptions.FilterWriteGroup).Build(this);

            this.saveCloseQuery = new EntityQueryBuilder(Allocator.Temp).WithAllRW<SaveCloseSubScene>()
                .WithOptions(EntityQueryOptions.FilterWriteGroup).WithAll(this.SaveType).Build(this);

            this.openLoadQuery = new EntityQueryBuilder(Allocator.Temp).WithAllRW<OpenLoadSubScene>()
                .WithOptions(EntityQueryOptions.FilterWriteGroup).WithAll(this.SaveType).Build(this);

            this.saveBufferArchetype = this.SaveType == default
                ? this.EntityManager.CreateArchetype(typeof(SaveBuffer))
                : this.EntityManager.CreateArchetype(typeof(SaveBuffer), this.SaveType);
        }

        /// <inheritdoc/>
        protected override void OnDestroy()
        {
            this.saveManager.Dispose();
        }

        /// <inheritdoc/>
        protected override void OnUpdate()
        {
            this.Dependency = this.saveManager.Update(this.Dependency);

            this.HandleLoadRequests();
            this.HandleOpenLoadRequests();
            this.HandleSaveCloseRequests();
            this.HandleSaveRequest();
        }

        private void HandleLoadRequests()
        {
            if (this.loadQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            if (this.loadQuery.CalculateEntityCount() != 1)
            {
                Debug.LogWarning("More than 1 load query");
                return;
            }

            var loadEntity = this.loadQuery.GetSingletonEntity();
            var saveData = SystemAPI.GetBuffer<Load>(loadEntity).Reinterpret<byte>().ToNativeArray(this.WorldUpdateAllocator);
            this.EntityManager.DestroyEntity(this.loadQuery);

            this.Dependency = this.saveManager.Load(saveData, this.Dependency);
        }

        private void HandleSaveRequest()
        {
            if (!this.saveQuery.HasSingleton<Save>())
            {
                return;
            }

            this.EntityManager.DestroyEntity(this.saveQuery);

            // Need to create it before we start the jobs
            var saveEntity = this.EntityManager.CreateEntity(this.saveBufferArchetype);
            var savedData = new NativeList<byte>(this.WorldUpdateAllocator);
            this.Dependency = this.saveManager.Save(ref savedData, this.Dependency);

            this.Dependency = new CopyToJob
                {
                    BufferEntity = saveEntity,
                    SaveBuffers = SystemAPI.GetBufferLookup<SaveBuffer>(),
                    SaveData = savedData,
                }
                .Schedule(this.Dependency);
        }

        private void HandleSaveCloseRequests()
        {
            if (this.saveCloseQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            var subScenes = this.saveCloseQuery.ToComponentDataArray<SaveCloseSubScene>(this.WorldUpdateAllocator);
            this.EntityManager.DestroyEntity(this.saveCloseQuery);
            this.Dependency = this.saveManager.SaveClose(subScenes.Reinterpret<Entity>(), this.Dependency);
        }

        private void HandleOpenLoadRequests()
        {
            if (this.openLoadQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            var subScenes = this.openLoadQuery.ToComponentDataArray<OpenLoadSubScene>(this.WorldUpdateAllocator);
            this.EntityManager.DestroyEntity(this.openLoadQuery);
            this.saveManager.OpenLoad(subScenes.Reinterpret<Entity>());
        }

        [BurstCompile]
        private struct CopyToJob : IJob
        {
            public Entity BufferEntity;
            public BufferLookup<SaveBuffer> SaveBuffers;

            [ReadOnly]
            public NativeList<byte> SaveData;

            public void Execute()
            {
                this.SaveBuffers[this.BufferEntity].Reinterpret<byte>().AddRange(this.SaveData.AsArray());
            }
        }
    }
}
