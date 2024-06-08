// <copyright file="SaveBuilder.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using System;
    using BovineLabs.Core.Assertions;
    using Unity.Collections;
    using Unity.Entities;

    /// <summary> SaveBuilder is a class for setting up various configurations for saving. </summary>
    public unsafe struct SaveBuilder : IDisposable
    {
        private NativeList<ComponentType> all;
        private NativeList<ComponentType> any;
        private NativeList<ComponentType> none;

        private NativeList<ComponentSaveState> componentSavers;
        private NativeList<ComponentSaveState> bufferSavers;

        // private readonly NativeList<ISaver> customSavers = new(); // TODO don't forget to clone
        private NativeList<EntityQuery> entityQueries;
        private NativeList<EntityQuery> unfilteredEntityQueries;

        private bool includeDisabled;
        private bool subSceneSaver;

        /// <summary>Initializes a new instance of the <see cref="SaveBuilder"/> struct. </summary>
        /// <param name="system"> The system that will own the <see cref="SaveProcessor"/> and its dependencies. </param>
        /// <param name="allocator"> The allocator to use. Generally <see cref="Allocator.Persistent"/>. </param>
        public SaveBuilder(ref SystemState system, Allocator allocator = Allocator.Persistent)
        {
            fixed (SystemState* ptr = &system)
            {
                this.SystemPtr = ptr;
            }

            this.all = new NativeList<ComponentType>(allocator);
            this.any = new NativeList<ComponentType>(allocator);
            this.none = new NativeList<ComponentType>(allocator);
            this.componentSavers = new NativeList<ComponentSaveState>(allocator);
            this.bufferSavers = new NativeList<ComponentSaveState>(allocator);
            this.entityQueries = new NativeList<EntityQuery>(allocator);
            this.unfilteredEntityQueries = new NativeList<EntityQuery>(allocator);

            this.includeDisabled = false;
            this.subSceneSaver = false;
            this.DisableAutoCreateSavers = false;
            this.DisableCompression = false;
            this.UseExistingInstances = false;
        }

        /// <summary> Gets the owning system. </summary>
        public ref SystemState System => ref *this.SystemPtr;

        public SystemState* SystemPtr { get; }

        // internal IEnumerable<ISaver> CustomSavers => this.customSavers; // TODO support again

        internal NativeArray<ComponentSaveState> ComponentSavers => this.componentSavers.AsArray();

        internal NativeArray<ComponentSaveState> BufferSavers => this.bufferSavers.AsArray();

        internal NativeArray<EntityQuery>.ReadOnly EntityQueries => this.entityQueries.AsArray().AsReadOnly();

        internal NativeArray<EntityQuery>.ReadOnly EntityUnfilteredQueries => this.unfilteredEntityQueries.AsArray().AsReadOnly();

        internal bool DisableAutoCreateSavers { get; private set; }

        internal bool DisableCompression { get; private set; }

        internal bool UseExistingInstances { get; private set; }

        /// <summary> Disposes all unmanaged memory. </summary>
        public void Dispose()
        {
            this.all.Dispose();
            this.any.Dispose();
            this.none.Dispose();
            this.componentSavers.Dispose();
            this.bufferSavers.Dispose();
            this.entityQueries.Dispose();
            this.unfilteredEntityQueries.Dispose();
        }

        /// <summary> Create a query from the current configuration. </summary>
        /// <param name="extraAllTypes"> Optional extra All types that can be included in the query. </param>
        /// <returns> A entity query linked to the system that owns the SaveBuilder. It does not need disposing as the system will handle it. </returns>
        public EntityQuery GetQuery(ReadOnlySpan<ComponentType> extraAllTypes = default)
        {
            var desc = this.CreateDescription(extraAllTypes);
            var query = desc.Build(ref this.System);
            this.AddQuery(query);
            return query;
        }

        /// <summary> Create a query from the current configuration. </summary>
        /// <param name="extraAllType"> Optional extra All type that can be included in the query. </param>
        /// <returns> A entity query linked to the system that owns the SaveBuilder. It does not need disposing as the system will handle it. </returns>
        public EntityQuery GetQuery(ComponentType extraAllType)
        {
            return this.GetQuery(new ReadOnlySpan<ComponentType>(&extraAllType, 1));
        }

        public void AddQuery(EntityQuery query)
        {
            this.entityQueries.Add(query);
        }

        /// <summary> Add new components to the All field in the EntityQueryDesc. </summary>
        /// <param name="type"> The component type to add. </param>
        /// <returns> Returns itself. </returns>
        public SaveBuilder WithAll(ComponentType type)
        {
            this.all.Add(type);
            return this;
        }

        /// <summary> Add new component to the All field in the EntityQueryDesc. </summary>
        /// <param name="isReadOnly"> Is the component readonly. </param>
        /// <typeparam name="T"> The component type to add. </typeparam>
        /// <returns> Returns itself. </returns>
        public SaveBuilder WithAll<T>(bool isReadOnly = true)
        {
            this.all.Add(isReadOnly ? ComponentType.ReadOnly<T>() : ComponentType.ReadWrite<T>());
            return this;
        }

        /// <summary> Add new components to the All field in the EntityQueryDesc. </summary>
        /// <param name="types"> The component types to add. </param>
        /// <returns> Returns itself. </returns>
        public SaveBuilder WithAll<T>(ref T types)
            where T : unmanaged, INativeList<ComponentType>
        {
            for (var i = 0; i < types.Length; i++)
            {
                this.all.Add(types[i]);
            }

            return this;
        }

        /// <summary> Add new components to the Any field in the EntityQueryDesc. </summary>
        /// <param name="type"> The component type to add. </param>
        /// <returns> Returns itself. </returns>
        public SaveBuilder WithAny(ComponentType type)
        {
            this.any.Add(type);
            return this;
        }

        /// <summary> Add new components to the Any field in the EntityQueryDesc.  </summary>
        /// <param name="isReadOnly"> Is the component readonly. </param>
        /// <typeparam name="T"> The component type to add. </typeparam>
        /// <returns> Returns itself. </returns>
        public SaveBuilder WithAny<T>(bool isReadOnly = true)
        {
            this.any.Add(isReadOnly ? ComponentType.ReadOnly<T>() : ComponentType.ReadWrite<T>());
            return this;
        }

        /// <summary> Add new components to the Any field in the EntityQueryDesc. </summary>
        /// <param name="types"> The component types to add. </param>
        /// <returns> Returns itself. </returns>
        public SaveBuilder WithAny<T>(ref T types)
            where T : unmanaged, INativeList<ComponentType>
        {
            for (var i = 0; i < types.Length; i++)
            {
                this.any.Add(types[i]);
            }

            return this;
        }

        /// <summary> Add new components to the None field in the EntityQueryDesc. </summary>
        /// <param name="type"> The component type to add. </param>
        /// <returns> Returns itself. </returns>
        public SaveBuilder WithNone(ComponentType type)
        {
            this.none.Add(type);
            return this;
        }

        /// <summary> Add new components to the None field in the EntityQueryDesc.  </summary>
        /// <param name="isReadOnly"> Is the component readonly. </param>
        /// <typeparam name="T"> The component type to add. </typeparam>
        /// <returns> Returns itself. </returns>
        public SaveBuilder WithNone<T>(bool isReadOnly = true)
        {
            this.none.Add(isReadOnly ? ComponentType.ReadOnly<T>() : ComponentType.ReadWrite<T>());
            return this;
        }

        /// <summary> Add new components to the None field in the EntityQueryDesc. </summary>
        /// <param name="types"> The component types to add. </param>
        /// <returns> Returns itself. </returns>
        public SaveBuilder WithNone<T>(ref T types)
            where T : unmanaged, INativeList<ComponentType>
        {
            for (var i = 0; i < types.Length; i++)
            {
                this.none.Add(types[i]);
            }

            return this;
        }

        // /// <summary> Add custom savers to save any extra data that can't be handled by the [Save] attribute. </summary>
        // /// <param name="savers"> The collection of savers to add. </param>
        // /// <returns> Returns itself. </returns>
        // public SaveBuilder WithSavers(params ISaver[] savers)
        // {
        //     this.customSavers.AddRange(savers);
        //     return this;
        // }

        public SaveBuilder WithComponentSaver<T>(SaveFeature feature = SaveFeature.None)
            where T : unmanaged, IComponentData
        {
            this.componentSavers.Add(new ComponentSaveState(ComponentType.ReadWrite<T>(), feature, null));
            return this;
        }

        public SaveBuilder WithBufferSaver<T>(SaveFeature feature = SaveFeature.None)
            where T : unmanaged, IBufferElementData
        {
            this.bufferSavers.Add(new ComponentSaveState(ComponentType.ReadWrite<T>(), feature, null));
            return this;
        }

        public SaveBuilder WithComponentSaver(ComponentSaveState state)
        {
            var componentType = ComponentType.FromTypeIndex(TypeManager.GetTypeIndexFromStableTypeHash(state.StableTypeHash));

            Check.Assume(!componentType.IsManagedComponent, "Saving managed states is not support");
            Check.Assume(componentType.IsComponent || componentType.IsBuffer, "Must be buffer or component");

            if (componentType.IsComponent)
            {
                this.componentSavers.Add(state);
            }
            else
            {
                this.bufferSavers.Add(state);
            }

            return this;
        }

        /// <summary>
        /// Should disabled entities also be saved? Note if this is set to true these entities will not be disabled automatically on loading the save.
        /// This needs to be handled by the user.
        /// </summary>
        /// <returns> Returns itself. </returns>
        public SaveBuilder WithIncludeDisabled()
        {
            this.includeDisabled = true;
            return this;
        }

        /// <summary> Is this builder used to save SubScenes. </summary>
        /// <returns> Returns itself. </returns>
        public SaveBuilder SubSceneSaver()
        {
            this.subSceneSaver = true;
            return this;
        }

        /// <summary> If set, only savers in <see cref="WithSavers"/> will be used. </summary>
        /// <returns> Returns itself. </returns>
        public SaveBuilder DoNotAutoCreateSavers()
        {
            this.DisableAutoCreateSavers = true;
            return this;
        }

        /// <summary>
        /// By default the file is compressed to reduce file size, this turns that off.
        /// This is useful for real time (rollback) saving and loading where maximum performance is required and file size isn't a concern.
        /// </summary>
        /// <returns> Returns itself. </returns>
        public SaveBuilder DoNotCompress()
        {
            this.DisableCompression = true;
            return this;
        }

        /// <summary>
        /// Instead of deleting prefab instances, deserialize on to them.
        /// Generally when loading a save you will have no prefab instances in the world so this does nothing except slows down loading while it checks.
        /// However if you are using the processor for rollback this can significantly improve performance by not having to delete then instantiate entities.
        /// </summary>
        /// <returns> Returns itself. </returns>
        public SaveBuilder ApplyToExistingPrefabInstances()
        {
            this.UseExistingInstances = true;
            return this;
        }

        /// <summary> Creates the <see cref="SaveProcessor"/> based off the configuration specified in this builder. </summary>
        /// <param name="allocator"> Allocator to use for the processor. </param>
        /// <returns> A new <see cref="SaveProcessor"/> instance. </returns>
        public SaveProcessor Create(Allocator allocator = Allocator.Persistent)
        {
            return new SaveProcessor(this, allocator);
        }

        public SaveBuilder Clone(Allocator allocator = Allocator.Persistent, bool includeSubSceneSaver = false)
        {
            var sb = new SaveBuilder(ref this.System, allocator);
            sb.all.AddRange(this.all.AsArray());
            sb.any.AddRange(this.any.AsArray());
            sb.none.AddRange(this.none.AsArray());
            sb.componentSavers.AddRange(this.componentSavers.AsArray());
            sb.bufferSavers.AddRange(this.bufferSavers.AsArray());
            sb.includeDisabled = this.includeDisabled;
            if (includeSubSceneSaver)
            {
                sb.subSceneSaver = this.subSceneSaver;
            }

            sb.DisableAutoCreateSavers = this.DisableAutoCreateSavers;
            sb.DisableCompression = this.DisableCompression;
            sb.UseExistingInstances = this.UseExistingInstances;
            return sb;
        }

        /// <summary> Create a query from the current configuration. </summary>
        /// <param name="extraAllTypes"> Optional extra All types that can be included in the query. </param>
        /// <returns> An entity query linked to the system that owns the SaveBuilder. It does not need disposing as the system will handle it. </returns>
        internal EntityQuery GetQueryUnfiltered(ReadOnlySpan<ComponentType> extraAllTypes = default)
        {
            var desc = this.CreateUnfilteredDescription(extraAllTypes);
            var query = desc.Build(ref this.System);
            this.unfilteredEntityQueries.Add(query);
            return query;
        }

        private EntityQueryBuilder CreateDescription(ReadOnlySpan<ComponentType> extraAllType)
        {
            var allTypes = new NativeList<ComponentType>(this.all.Length, Allocator.Temp);
            var noneTypes = new NativeList<ComponentType>(this.none.Length, Allocator.Temp);
            var anyTypes = new NativeList<ComponentType>(this.any.Length, Allocator.Temp);

            allTypes.AddRange(this.all.AsArray());
            anyTypes.AddRange(this.any.AsArray());
            noneTypes.AddRange(this.none.AsArray());

            allTypes.Add(ComponentType.ReadOnly<Savable>());

            if (this.subSceneSaver)
            {
                allTypes.Add(ComponentType.ReadOnly<SceneSection>());
            }
            else
            {
                noneTypes.Add(ComponentType.ReadOnly<SceneSection>());
            }

            foreach (var c in extraAllType)
            {
                allTypes.Add(c);
            }

            return new EntityQueryBuilder(Allocator.Temp)
                .WithAll(ref allTypes)
                .WithNone(ref noneTypes)
                .WithAny(ref anyTypes)
                .WithOptions((this.includeDisabled ? EntityQueryOptions.IncludeDisabledEntities : EntityQueryOptions.Default) |
                             EntityQueryOptions.IgnoreComponentEnabledState);
        }

        private EntityQueryBuilder CreateUnfilteredDescription(ReadOnlySpan<ComponentType> extraAllType)
        {
            var allTypes = new NativeList<ComponentType>(this.all.Length, Allocator.Temp);
            var noneTypes = new NativeList<ComponentType>(this.none.Length, Allocator.Temp);

            if (this.subSceneSaver)
            {
                allTypes.Add(ComponentType.ReadOnly<SceneSection>());
            }
            else
            {
                noneTypes.Add(ComponentType.ReadOnly<SceneSection>());
            }

            foreach (var c in extraAllType)
            {
                allTypes.Add(c);
            }

            return new EntityQueryBuilder(Allocator.Temp)
                .WithAll(ref allTypes)
                .WithNone(ref noneTypes)
                .WithOptions((this.includeDisabled ? EntityQueryOptions.IncludeDisabledEntities : EntityQueryOptions.Default) |
                             EntityQueryOptions.IgnoreComponentEnabledState);
        }
    }
}
