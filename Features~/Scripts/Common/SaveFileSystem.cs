// <copyright file="SaveFileSystem.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Common
{
    using System.Collections.Generic;
    using BovineLabs.Saving.Samples.Filter;
    using BovineLabs.Saving.Samples.Rollback;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Scenes;

    public partial class SaveFileSystem : SystemBase
    {
        private Dictionary<ComponentType, NativeArray<byte>> saveFiles = new Dictionary<ComponentType, NativeArray<byte>>();
        private EntityQuery saveDataQuery;

        public void Load(ComponentType componentType)
        {
            if (!this.saveFiles.TryGetValue(componentType, out var saveData))
            {
                return;
            }

            if (saveData.Length == 0)
            {
                return;
            }

            var entity = componentType != default
                ? this.EntityManager.CreateEntity(typeof(Load), componentType)
                : this.EntityManager.CreateEntity(typeof(Load));

            var buffer = SystemAPI.GetBuffer<Load>(entity);
            buffer.Reinterpret<byte>().AddRange(saveData);

        }

        public void Load(NativeArray<byte> data, ComponentType componentType)
        {
            if (this.saveFiles.TryGetValue(componentType, out var saveData))
            {
                saveData.Dispose();
            }

            this.saveFiles[componentType] = data;
            this.Load(componentType);
        }

        protected override void OnCreate()
        {
            this.saveDataQuery = SystemAPI.QueryBuilder()
                .WithAll<SaveBuffer>()
                .WithNone<RollbackSave>()
                .Build();

            this.RequireForUpdate<SaveSample>();
            this.RequireForUpdate(this.saveDataQuery);
        }

        /// <inheritdoc/>
        protected override void OnDestroy()
        {
            foreach (var s in this.saveFiles)
            {
                s.Value.Dispose();
            }
        }

        /// <inheritdoc/>
        protected override void OnUpdate()
        {
            var entity = this.saveDataQuery.GetSingletonEntity();
            var buffer = SystemAPI.GetBuffer<SaveBuffer>(entity);

            if (this.SaveReplace(entity, buffer, typeof(FilterComponentSave)) ||
                this.SaveReplace(entity, buffer, typeof(FilterSharedComponentSave)) ||
                this.SaveReplace(entity, buffer, default))
            {
                this.EntityManager.DestroyEntity(entity);
            }
        }

        private bool SaveReplace(Entity entity, DynamicBuffer<SaveBuffer> buffer, ComponentType componentType)
        {
            if (componentType == default || this.EntityManager.HasComponent(entity, componentType))
            {
                if (this.saveFiles.TryGetValue(componentType, out var saveData))
                {
                    saveData.Dispose();
                }

                this.saveFiles[componentType] = buffer.Reinterpret<byte>().ToNativeArray(Allocator.Persistent);
                return true;
            }

            return false;
        }
    }
}
