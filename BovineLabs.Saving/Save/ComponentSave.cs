// <copyright file="ComponentSave.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using BovineLabs.Core.Assertions;
    using Unity.Assertions;
    using Unity.Burst.Intrinsics;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using UnityEngine;

    public unsafe struct ComponentSave : IDisposable
    {
        private EntityQuery commandBufferQuery;

        public ComponentSave(ref SystemState state, SaveBuilder builder, ComponentSaveState saveState)
        {
            this.TypeIndex = TypeManager.GetTypeIndexFromStableTypeHash(saveState.StableTypeHash);
            this.TypeInfo = TypeManager.GetTypeInfo(this.TypeIndex);

            this.Feature = saveState.Feature;

            Assert.IsFalse(this.TypeIndex.IsManagedType);

            this.QueryWrite = builder.GetQuery(ref state, ComponentType.ReadWrite(this.TypeIndex));

            this.EntityOffsets = default;
            this.SaveChunks = default;

            if (this.Feature != SaveFeature.None)
            {
                this.commandBufferQuery = new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<EndInitializationEntityCommandBufferSystem.Singleton>()
                    .WithOptions(EntityQueryOptions.IncludeSystems)
                    .Build(ref state);
            }
            else
            {
                this.commandBufferQuery = default;
            }

            this.CreateComponentInfo(saveState);
        }

        public SaveFeature Feature { get; }

        public TypeIndex TypeIndex { get; }

        public TypeManager.TypeInfo TypeInfo { get; }

        public NativeArray<SaveChunk> SaveChunks { get; private set; }

        public NativeArray<int> EntityOffsets { get; private set; }

        public EntityQuery QueryWrite { get; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemapEntityField([ReadOnly] byte* startPtr, int offset, EntityMap remap)
        {
            var entity = (Entity*)(startPtr + offset);
            *entity = remap.TryGetEntity(*entity, out var newEntity) ? newEntity : Entity.Null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSet(v128 v, int pos)
        {
            var ptr = (ulong*)&v;
            var idx = pos >> 6;
            var shift = pos & 0x3f;
            var mask = 1ul << shift;
            return (ptr[idx] & mask) != 0ul;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.SaveChunks.Dispose();
            this.EntityOffsets.Dispose();
        }

        public EntityCommandBuffer CreateCommandBuffer(ref SystemState state)
        {
            return this.commandBufferQuery.GetSingleton<EndInitializationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
        }

        private void CreateComponentInfo(ComponentSaveState state)
        {
            var type = TypeManager.GetType(this.TypeIndex);

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var chunks = new List<SaveChunk>();
            var startIndex = 0;

            if (state.SaveIgnoreLength > 0)
            {
                Check.Assume(fields.Length <= byte.MaxValue);

                for (byte index = 0; index < fields.Length; index++)
                {
                    var field = fields[index];
                    var offset = UnsafeUtility.GetFieldOffset(field);
                    var size = UnsafeUtility.SizeOf(field.FieldType);

                    if (state.IsIgnored(index))
                    {
                        // Offset will equal startIndex if ignored is on the first field or if there are multiple SaveIgnore fields in a row
                        if (offset > startIndex)
                        {
                            chunks.Add(new SaveChunk(startIndex, offset - startIndex));
                        }

                        startIndex = offset + size;
                    }
                }
            }
            else
            {
                foreach (var field in fields)
                {
                    var offset = UnsafeUtility.GetFieldOffset(field);
                    var size = UnsafeUtility.SizeOf(field.FieldType);

                    if (field.IsDefined(typeof(SaveIgnoreAttribute), false))
                    {
                        // Offset will equal startIndex if SaveIgnore is on the first field or if there are multiple SaveIgnore fields in a row
                        if (offset > startIndex)
                        {
                            chunks.Add(new SaveChunk(startIndex, offset - startIndex));
                        }

                        startIndex = offset + size;
                    }
                }
            }

            // If we aren't at end we need to add 1 more chunk. This also handles the case with no SaveIgnore attributes.
            if (startIndex != UnsafeUtility.SizeOf(type))
            {
                chunks.Add(new SaveChunk(startIndex, UnsafeUtility.SizeOf(type) - startIndex));
            }

            // Enforce LayoutSequential for any component using SaveIgnore
            if (chunks.Count > 1 && !type.IsLayoutSequential)
            {
                Debug.LogError($"{nameof(SaveIgnoreAttribute)} used on {type.Name} which is not using LayoutSequential and will be ignored.");
                this.SaveChunks = new NativeArray<SaveChunk>(1, Allocator.Persistent) { [0] = new(0, this.TypeInfo.ElementSize) };
            }
            else
            {
                var saveChunks = new NativeArray<SaveChunk>(chunks.Count, Allocator.Persistent);
                for (var i = 0; i < chunks.Count; i++)
                {
                    saveChunks[i] = chunks[i];
                }

                this.SaveChunks = saveChunks;
            }

            if (!TypeManager.HasEntityReferences(this.TypeIndex))
            {
                this.EntityOffsets = new NativeArray<int>(0, Allocator.Persistent);
            }
            else
            {
                var offsets = TypeManager.GetEntityOffsets(this.TypeIndex, out var offsetCount);

                var offsetList = new List<int>(offsetCount);
                for (var i = 0; i < offsetCount; i++)
                {
                    var offset = offsets[i].Offset;

                    foreach (var chunk in this.SaveChunks)
                    {
                        if (offset < chunk.Index || offset >= chunk.Index + chunk.Length)
                        {
                            continue;
                        }

                        offsetList.Add(offset);
                        break;
                    }
                }

                var entityOffsets = new NativeArray<int>(offsetList.Count, Allocator.Persistent);
                for (var index = 0; index < offsetList.Count; index++)
                {
                    entityOffsets[index] = offsetList[index];
                }

                this.EntityOffsets = entityOffsets;
            }
        }

        public struct HeaderComponent
        {
            /// <summary> The number of elements in the save. Generally the entity count. </summary>
            public int Count;

            /// <summary> Size info about each element. Used for component saving. </summary>
            public int ElementSize;

            /// <summary>
            ///
            /// </summary>
            public bool IsEnableable;

            public fixed byte Padding[7];
        }

        public readonly struct SaveChunk
        {
            public readonly int Index;
            public readonly int Length;

            public SaveChunk(int index, int length)
            {
                this.Index = index;
                this.Length = length;
            }

            public override string ToString()
            {
                return $"{nameof(this.Index)} = {this.Index}, {nameof(this.Length)} = {this.Length}";
            }
        }

        public struct HeaderChunk
        {
            public int Length;

            // Reserved for future
            public fixed byte Padding[4];
        }
    }
}
