﻿// <copyright file="SaveProcessor.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using BovineLabs.Core.Extensions;
    using BovineLabs.Core.Internal;
    using BovineLabs.Core.Utility;
    using BovineLabs.Saving.Migrate;
    using Unity.Assertions;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Profiling;
    using Unity.Transforms;
    using UnityEngine;

    public unsafe struct SaveProcessor : IDisposable
    {
        private static readonly ProfilerMarker SerializeMarker = new("Serialize");
        private static readonly ProfilerMarker CompressMarker = new("Compress");
        private static readonly ProfilerMarker MergeMarker = new("Merge");

        private static readonly ProfilerMarker DecompressMarker = new("Decompress");
        private static readonly ProfilerMarker DeserializeMarker = new("Deserialize");

        private readonly bool disableCompression;

        private SaveBuilder builder;
        private EntityQuery sharedQuery;
        private SceneSection subSceneFilter;

        private SavablePrefabSaver savablePrefabSaver;
        private SavableSceneSaver savableSceneSaver;
        private NativeHashMap<ulong, ComponentDataSave> componentSavers;
        private NativeHashMap<ulong, BufferElementDataSave> bufferSavers;

        private NativeHashMap<ulong, FunctionPointer<MigrateDelegate>> migrators;

        private NativeList<(ulong Key, Serializer Serializer)> serializerMap;

        internal SaveProcessor(ref SystemState state, SaveBuilder saveBuilder, Allocator allocator = Allocator.Persistent)
        {
            this.builder = saveBuilder;

            this.componentSavers = new NativeHashMap<ulong, ComponentDataSave>(0, allocator);
            this.bufferSavers = new NativeHashMap<ulong, BufferElementDataSave>(0, allocator);
            this.migrators = new NativeHashMap<ulong, FunctionPointer<MigrateDelegate>>(0, allocator);

            this.serializerMap = new NativeList<(ulong Key, Serializer Serializer)>(allocator);

            this.disableCompression = this.builder.DisableCompression;
            this.sharedQuery = this.builder.GetQuery(ref state);
            this.subSceneFilter = default;

            this.savablePrefabSaver = new SavablePrefabSaver(ref state, this.builder);
            this.savableSceneSaver = new SavableSceneSaver(ref state, this.builder);

            this.SetupAllSavers(ref state);
            this.SetupAllMigrators();
        }

        private int SaverCount => this.componentSavers.Count + this.bufferSavers.Count + 3;

        public void Dispose()
        {
            using var cs = this.componentSavers.GetEnumerator();
            while (cs.MoveNext())
            {
                cs.Current.Value.Dispose();
            }

            using var bs = this.bufferSavers.GetEnumerator();
            while (bs.MoveNext())
            {
                bs.Current.Value.Dispose();
            }

            this.builder.Dispose();

            this.componentSavers.Dispose();
            this.bufferSavers.Dispose();
            this.migrators.Dispose();
            this.serializerMap.Dispose();
        }

        public JobHandle Save(ref SystemState state, NativeList<byte> saveData, JobHandle dependency)
        {
            dependency = this.Serialize(ref state, dependency);
            dependency = this.Compress(dependency);
            dependency = this.Merge(ref state, saveData, dependency);

            return dependency;
        }

        public JobHandle Load(ref SystemState state, NativeArray<byte> compressed, JobHandle dependency)
        {
            var decompressed = this.Decompress(ref state, compressed);
            var deserializers = this.Split(ref state, decompressed);
            dependency = this.Deserialize(ref state, deserializers, dependency);

            return dependency;
        }

        public void ResetFilter()
        {
            foreach (var query in this.builder.EntityQueries)
            {
                query.ResetFilter();
            }

            foreach (var query in this.builder.EntityUnfilteredQueries)
            {
                query.ResetFilter();
            }

            this.subSceneFilter = default;
        }

        public void SetSharedComponentFilter<T>(T sharedComponent)
            where T : unmanaged, ISharedComponentData
        {
            if (sharedComponent is SceneSection)
            {
                Debug.LogError("Use SetSubSceneFilter for SubSceneFiltering");
                return;
            }

            foreach (var query in this.builder.EntityQueries)
            {
                // We have a saved sub scene filter, ensure we add it back
                if (this.subSceneFilter.SceneGUID != default)
                {
                    query.ResetFilter();
                    query.SetSharedComponentFilter(sharedComponent, this.subSceneFilter);
                }
                else
                {
                    query.SetSharedComponentFilter(sharedComponent);
                }
            }
        }

        public void SetSubSceneFilter(SceneSection sectionSection)
        {
            foreach (var query in this.builder.EntityQueries)
            {
                this.SetSectionFilterOnQuery(query, sectionSection);
            }

            foreach (var query in this.builder.EntityUnfilteredQueries)
            {
                this.SetSectionFilterOnQuery(query, sectionSection);
            }

            this.subSceneFilter = sectionSection;
        }

        internal static NativeList<ComponentSaveState> BuiltInSavers(Allocator allocator = Allocator.Temp)
        {
            return new NativeList<ComponentSaveState>(allocator)
            {
                // Add custom savers for common components we don't have control over
                new(ComponentType.ReadWrite<LocalTransform>()),
                new(ComponentType.ReadWrite<Disabled>(), SaveFeature.AddComponent),
#if UNITY_PHYSICS
                new(ComponentType.ReadWrite<Unity.Physics.PhysicsVelocity>()),
#endif
            };
        }

        private static (ulong From, MigrateDelegate Migrator) CreateMigrator((MethodInfo Method, MigrateAttribute Attribute) m)
        {
            var result = (MigrateDelegate)Delegate.CreateDelegate(typeof(MigrateDelegate), m.Method, false);
            if (result == null)
            {
                Debug.LogError($"Unable to create migrator for {m.Method.Name} likely due to parameter miss match");
            }

            return (m.Attribute.From, result);
        }

        private void SetSectionFilterOnQuery(EntityQuery query, SceneSection sectionSection)
        {
            ref var sharedFilters = ref query.GetSharedFiltersAsRef();

            switch (sharedFilters.Count)
            {
                case 0:
                    query.SetSharedComponentFilter(sectionSection);
                    break;

                case 1:
                    if (this.subSceneFilter.SceneGUID != default)
                    {
                        Assert.IsTrue(
                            query.QueryHasSharedFilter<SceneSection>(0),
                            "A SceneSection SharedComponentFilter has been overridden incorrectly.");

                        // Only filter is already a sub scene so just replace
                        // Otherwise add to end
                        query.SetSharedComponentFilter(sectionSection);
                    }
                    else
                    {
                        Assert.IsFalse(
                            query.QueryHasSharedFilter<SceneSection>(0),
                            "A SceneSection SharedComponentFilter has been added incorrectly.");

                        // Otherwise add to end
                        query.AddSharedComponentFilter(sectionSection);
                    }

                    break;

                case 2:
                    Assert.IsTrue(
                        query.QueryHasSharedFilter<SceneSection>(1),
                        "A query has been manually filtered and is not supported. This must be done via SaveProcessor.");

                    // Replace existing and SetSharedComponentFilter ensures the SceneSection is always the last index
                    query.ReplaceSharedComponentFilter(1, sectionSection);
                    break;
            }
        }

        private void SetupAllSavers(ref SystemState state)
        {
            if (this.builder.DisableAutoCreateSavers)
            {
                return;
            }

            foreach (var saver in BuiltInSavers(state.WorldUpdateAllocator).AsArray())
            {
                ref readonly var type = ref TypeManager.GetTypeInfo(TypeManager.GetTypeIndexFromStableTypeHash(saver.StableTypeHash));

                switch (type.Category)
                {
                    case TypeManager.TypeCategory.ComponentData:
                        this.AddComponentSaver(ref state, saver);
                        break;
                    case TypeManager.TypeCategory.BufferData:
                        this.AddBufferSaver(ref state, saver);
                        break;
                }
            }

            foreach (var type in this.builder.ComponentSavers)
            {
                this.AddComponentSaver(ref state, type);
            }

            foreach (var type in this.builder.BufferSavers)
            {
                this.AddBufferSaver(ref state, type);
            }

            foreach (var type in TypeManager.AllTypes)
            {
                if (type.TypeIndex == 0)
                {
                    continue;
                }

                if (type.Category == TypeManager.TypeCategory.ComponentData)
                {
                    var saveAttribute = type.Type.GetCustomAttribute<SaveAttribute>();
                    if (saveAttribute != null)
                    {
                        this.AddComponentSaver(ref state, new ComponentSaveState(ComponentType.FromTypeIndex(type.TypeIndex), saveAttribute.Feature));
                    }
                }
                else if (type.Category == TypeManager.TypeCategory.BufferData)
                {
                    var saveAttribute = type.Type.GetCustomAttribute<SaveAttribute>();
                    if (saveAttribute != null)
                    {
                        this.AddBufferSaver(ref state, new ComponentSaveState(ComponentType.FromTypeIndex(type.TypeIndex), saveAttribute.Feature));
                    }
                }
            }

            // TODO
            // Add any user custom savers passed in
            // foreach (var saver in this.builder.CustomSavers)
            // {
            //     this.savers.Add(saver.Key, saver);
            // }
        }

        /// <summary>
        /// This finds all methods that have been setup as function pointers with the <see cref="MigrateAttribute"/>.
        /// 1. Find all assemblies that reference this assembly which is required for the  <see cref="MigrateAttribute"/>.
        /// 2. Find all static classes that have <see cref="BurstCompileAttribute"/> as we require this to be burst compiled.
        /// 3. Find any methods inside these classes that have both <see cref="BurstCompileAttribute"/> and <see cref="MigrateAttribute"/>.
        /// 4. Create a <see cref="MigrateDelegate"/> from this method. If the signature doesn't match it will return null.
        /// 5. Populate our map with From and <see cref="MigrateDelegate"/> pairs to be used for migration.
        /// </summary>
        private void SetupAllMigrators()
        {
            var delegates = ReflectionUtility.GetAllAssemblyWithReference(this.GetType().Assembly)
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsClass && t.IsAbstract && t.IsSealed) // Find all static classes
                .Where(t => t.GetCustomAttribute<BurstCompileAttribute>() != null)
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(t => t.GetCustomAttribute<BurstCompileAttribute>() != null && !t.Name.EndsWith("BurstManaged")) // 1.8.3 returns default + burst methods
                .Select(m => (MethodInfo: m, Attribute: m.GetCustomAttribute<MigrateAttribute>()))
                .Where(m => m.Attribute != null && m.Attribute.From != 0)
                .Select(CreateMigrator)
                .Where(m => m.Migrator != null);

            foreach (var (from, migrator) in delegates)
            {
                this.migrators.Add(from, BurstCompiler.CompileFunctionPointer(migrator));
            }
        }

        private void AddComponentSaver(ref SystemState state, ComponentSaveState saveState)
        {
            var saver = new ComponentDataSave(ref state, this.builder, saveState);
            this.componentSavers.Add(saver.Key, saver);
        }

        private void AddBufferSaver(ref SystemState state, ComponentSaveState saveState)
        {
            var saver = new BufferElementDataSave(ref state, this.builder, saveState);
            this.bufferSavers.Add(saver.Key, saver);
        }

        private JobHandle Serialize(ref SystemState state, JobHandle dependency)
        {
            using (SerializeMarker.Auto())
            {
                this.serializerMap.Clear();

                var handles = new NativeArray<JobHandle>(this.SaverCount, Allocator.Temp);
                var index = 0;

                var chunks = this.sharedQuery.ToArchetypeChunkListAsync(state.WorldUpdateAllocator, dependency, out var chunkDependency);
                dependency = chunkDependency;

                handles[index++] = this.Serialize(ref state, this.savablePrefabSaver, chunks, dependency);
                handles[index++] = this.Serialize(ref state, this.savableSceneSaver, chunks, dependency);

                foreach (var saver in this.componentSavers)
                {
                    handles[index++] = this.Serialize(ref state, saver.Value, chunks, dependency);
                }

                foreach (var saver in this.bufferSavers)
                {
                    handles[index++] = this.Serialize(ref state, saver.Value, chunks, dependency);
                }

                dependency = JobHandle.CombineDependencies(handles);

                return dependency;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private JobHandle Serialize<T>(ref SystemState state, T saver, in NativeList<ArchetypeChunk> chunks, JobHandle dependency)
            where T : unmanaged, ISaver
        {
            var (serializer, newDependency) = saver.Serialize(ref state, chunks, dependency);
            this.serializerMap.Add((saver.Key, serializer));
            return newDependency;
        }

        private JobHandle Compress(JobHandle dependency)
        {
            if (this.disableCompression)
            {
                return dependency;
            }

            using (CompressMarker.Auto())
            {
                var handles = new NativeArray<JobHandle>(this.SaverCount, Allocator.Temp);

                for (var index = 0; index < this.serializerMap.Length; index++)
                {
                    var (_, serializer) = this.serializerMap[index];
                    handles[index] = new CompressJob
                        {
                            SaveData = serializer.Data,
                        }
                        .Schedule(dependency);
                }

                dependency = JobHandle.CombineDependencies(handles);

                return dependency;
            }
        }

        private JobHandle Merge(ref SystemState state, NativeList<byte> saveData, JobHandle dependency)
        {
            using (MergeMarker.Auto())
            {
                var keyIndexMap = new NativeHashMap<ulong, int>(this.serializerMap.Length, state.WorldUpdateAllocator);
                var startIndex = new NativeReference<int>(state.WorldUpdateAllocator);
                startIndex.Value = UnsafeUtility.SizeOf<Header>();

                var header = new Header
                {
                    IsCompressed = !this.disableCompression,
                };

                foreach (var (key, serializer) in this.serializerMap)
                {
                    dependency = new CalculateKeyIndexMapJob
                        {
                            KeyIndexMap = keyIndexMap,
                            StartIndex = startIndex,
                            SaveData = serializer.Data,
                            Key = key,
                        }
                        .Schedule(dependency);
                }

                dependency = new ResizeOutputSetHeaderJob
                    {
                        SaveData = saveData,
                        Length = startIndex,
                        Header = header,
                    }
                    .Schedule(dependency);

                var handles = new NativeArray<JobHandle>(this.serializerMap.Length, Allocator.Temp);

                for (var index = 0; index < this.serializerMap.Length; index++)
                {
                    var (key, serializer) = this.serializerMap[index];

                    var newDependency = new MergeSaveDataJob
                        {
                            Output = saveData,
                            Input = serializer.Data,
                            KeyIndexMap = keyIndexMap,
                            Key = key,
                        }
                        .Schedule(dependency);

                    handles[index] = newDependency;
                    UnsafeListDispose.Dispose(serializer.Data, newDependency);
                }

                dependency = JobHandle.CombineDependencies(handles);

                return dependency;
            }
        }

        private NativeArray<byte> Decompress(ref SystemState state, NativeArray<byte> compressed)
        {
            using (DecompressMarker.Auto())
            {
                var headerSize = UnsafeUtility.SizeOf<Header>();

                if (compressed.Length < headerSize)
                {
                    return CollectionHelper.CreateNativeArray<byte>(0, state.WorldUpdateAllocator);
                }

                var header = new Deserializer(compressed, 0).Read<Header>();
                compressed = compressed.GetSubArray(headerSize, compressed.Length - headerSize);

                if (!header.IsCompressed)
                {
                    var clone = CollectionHelper.CreateNativeArray<byte>(compressed.Length, state.WorldUpdateAllocator);
                    clone.CopyFrom(compressed); // still need to copy for dispose safety
                    return clone;
                }

                if (compressed.Length < UnsafeUtility.SizeOf<HeaderCompression>())
                {
                    return CollectionHelper.CreateNativeArray<byte>(0, state.WorldUpdateAllocator);
                }

                var deserializer = new Deserializer(compressed, 0);

                var totalSize = 0;
                var data = new NativeList<DecompressData>(16, state.WorldUpdateAllocator);

                while (!deserializer.IsAtEnd)
                {
                    var compHeader = deserializer.Peek<HeaderCompression>();

                    data.Add(new DecompressData
                    {
                        CompressedIndex = deserializer.CurrentIndex,
                        DecompressedIndex = totalSize,
                    });

                    totalSize += compHeader.UncompressedLength;

                    deserializer.Offset(UnsafeUtility.SizeOf<HeaderCompression>());
                    deserializer.Offset(compHeader.CompressedLength);
                }

                var decompressed = CollectionHelper.CreateNativeArray<byte>(totalSize, state.WorldUpdateAllocator);

                var dependency = new DecompressJob
                    {
                        Data = data.AsArray(),
                        Compressed = compressed,
                        Decompressed = decompressed,
                    }
                    .ScheduleParallel(data.Length, 1, default);

                dependency.Complete();

                return decompressed;
            }
        }

        private DeserializeMap Split(ref SystemState state, NativeArray<byte> decompressed)
        {
            var deserializer = new Deserializer(decompressed);

            var map = new DeserializeMap(state.WorldUpdateAllocator);

            while (!deserializer.IsAtEnd)
            {
                var header = deserializer.Peek<HeaderSaver>();
                var saveDeserializer = new Deserializer(decompressed, deserializer.CurrentIndex);

                this.TryGetSaver(ref state, ref saveDeserializer, ref map);

                // Don't let bad data infinite loop us
                if (header.LengthInBytes == 0)
                {
                    Debug.LogError("Infinite loop detected breaking");
                    return map;
                }

                deserializer.Offset(header.LengthInBytes);
            }

            return map;
        }

        private JobHandle Deserialize(ref SystemState state, DeserializeMap deserializers, JobHandle dependency)
        {
            using (DeserializeMarker.Auto())
            {
                var entityMapper = new EntityMap(state.WorldUpdateAllocator);

                dependency = this.savablePrefabSaver.Deserialize(ref state, deserializers.SavablePrefabSaver, entityMapper, dependency);
                dependency = this.savableSceneSaver.Deserialize(ref state, deserializers.SavableSceneSaver, entityMapper, dependency);

                // We allow component stage to run in parallel with the intentional of writing to different components
                // Every other stage runs sequentially
                var count = deserializers.Components.Length + deserializers.Buffers.Length;
                var handles = new NativeArray<JobHandle>(count, Allocator.Temp);
                var index = 0;

                foreach (var deserializer in deserializers.Components)
                {
                    handles[index++] = this.componentSavers[deserializer.Key].Deserialize(ref state, deserializer.Deserializer, entityMapper, dependency);
                }

                foreach (var deserializer in deserializers.Buffers)
                {
                    handles[index++] = this.bufferSavers[deserializer.Key].Deserialize(ref state, deserializer.Deserializer, entityMapper, dependency);
                }

                dependency = JobHandle.CombineDependencies(handles);

                return dependency;
            }
        }

        private bool TryGetSaver(ref SystemState state, ref Deserializer deserializer, ref DeserializeMap map)
        {
            while (true)
            {
                var header = deserializer.Peek<HeaderSaver>();

                if (this.componentSavers.ContainsKey(header.Key))
                {
                    map.Components.Add((header.Key, deserializer));
                    return true;
                }

                if (this.bufferSavers.ContainsKey(header.Key))
                {
                    map.Buffers.Add((header.Key, deserializer));
                    return true;
                }

                if (this.savablePrefabSaver.Key == header.Key)
                {
                    map.SavablePrefabSaver = deserializer;
                    return true;
                }

                if (this.savableSceneSaver.Key == header.Key)
                {
                    map.SavableSceneSaver = deserializer;
                    return true;
                }

                if (!this.TryMigrate(ref state, ref deserializer))
                {
                    Debug.LogWarning(
                        $"No saver or migration was found or the migration failed for {header.Key}. This data will not be deserialized. If intentional ignore.");
                    return false;
                }

                // Successfully migrated, deserializer will be updated and point to new data, try find a saver again
                // It's to migrate multiple times on old data
            }
        }

        private bool TryMigrate(ref SystemState state, ref Deserializer deserializer)
        {
            var header = deserializer.Peek<HeaderSaver>();
            if (!this.migrators.TryGetValue(header.Key, out var migrator) || !migrator.Invoke(ref state, ref deserializer))
            {
                return false;
            }

            // Debug.Log($"Migrated from {header.Key} to {migrator.To}");
            return true;
        }

        [BurstCompile]
        private struct CompressJob : IJob
        {
            [NativeDisableUnsafePtrRestriction]
            public UnsafeList<byte>* SaveData;

            public void Execute()
            {
                var uncompressedLength = this.SaveData->Length;

                if (uncompressedLength == 0)
                {
                    return;
                }

                var src = this.SaveData->Ptr;
                var compressedLength = CodecService.Compress(Codec.LZ4, src, this.SaveData->Length, out var dst);

                this.SaveData->Clear();

                var header = new HeaderCompression { UncompressedLength = uncompressedLength, CompressedLength = compressedLength };
                this.SaveData->AddRange(&header, UnsafeUtility.SizeOf<HeaderCompression>());
                this.SaveData->AddRange(dst, compressedLength);
            }
        }

        [BurstCompile]
        private struct DecompressJob : IJobFor
        {
            [ReadOnly]
            public NativeArray<DecompressData> Data;

            [ReadOnly]
            public NativeArray<byte> Compressed;

            [NativeDisableParallelForRestriction]
            public NativeArray<byte> Decompressed;

            public void Execute(int index)
            {
                var data = this.Data[index];

                var compressed = (byte*)this.Compressed.GetUnsafeReadOnlyPtr() + data.CompressedIndex;
                var compressionInfo = UnsafeUtility.AsRef<HeaderCompression>(compressed);
                var src = compressed + UnsafeUtility.SizeOf<HeaderCompression>();

                var decompressed = (byte*)this.Decompressed.GetUnsafeReadOnlyPtr();
                var dst = decompressed + data.DecompressedIndex;

                var success = CodecService.Decompress(Codec.LZ4, src, compressionInfo.CompressedLength, dst, compressionInfo.UncompressedLength);

                if (!success)
                {
                    Debug.LogError("Failed to decompress save");
                }
            }
        }

        [BurstCompile]
        private struct CalculateKeyIndexMapJob : IJob
        {
            public NativeHashMap<ulong, int> KeyIndexMap;
            public NativeReference<int> StartIndex;

            [ReadOnly]
            [NativeDisableUnsafePtrRestriction]
            public UnsafeList<byte>* SaveData;

            public ulong Key;

            public void Execute()
            {
                this.KeyIndexMap.Add(this.Key, this.StartIndex.Value);
                this.StartIndex.Value += this.SaveData->Length;
            }
        }

        [BurstCompile]
        private struct ResizeOutputSetHeaderJob : IJob
        {
            [ReadOnly]
            public NativeReference<int> Length;

            public NativeList<byte> SaveData;

            public Header Header;

            public void Execute()
            {
                this.SaveData.ResizeUninitialized(this.Length.Value);

                var ptr = (Header*)this.SaveData.GetUnsafePtr();
                *ptr = this.Header;
            }
        }

        [BurstCompile]
        private struct MergeSaveDataJob : IJob
        {
            // We write from multiple threads but ensure no overlap using the KeyIndexMap
            [NativeDisableContainerSafetyRestriction]
            public NativeList<byte> Output;

            [ReadOnly]
            [NativeDisableUnsafePtrRestriction]
            public UnsafeList<byte>* Input;

            [ReadOnly]
            public NativeHashMap<ulong, int> KeyIndexMap;

            public ulong Key;

            public void Execute()
            {
                var index = this.KeyIndexMap[this.Key];
                UnsafeUtility.MemCpy(this.Output.GetUnsafePtr() + index, this.Input->Ptr, this.Input->Length);
            }
        }

        private struct HeaderCompression
        {
            public int UncompressedLength;
            public int CompressedLength;

            private fixed byte buffer[8];
        }

        private struct Header
        {
            public bool IsCompressed;

            private fixed byte buffer[12];
        }

        private struct DecompressData
        {
            public int CompressedIndex;
            public int DecompressedIndex;
        }

        private struct DeserializeMap
        {
            public NativeList<(ulong Key, Deserializer Deserializer)> Components;
            public NativeList<(ulong Key, Deserializer Deserializer)> Buffers;
            public Deserializer SavablePrefabSaver;
            public Deserializer SavableSceneSaver;

            public DeserializeMap(Allocator allocator)
            {
                this.Components = new NativeList<(ulong, Deserializer)>(allocator);
                this.Buffers = new NativeList<(ulong, Deserializer)>(allocator);
                this.SavablePrefabSaver = default;
                this.SavableSceneSaver = default;
            }
        }
    }
}
