namespace BovineLabs.Saving
{
    using System;
    using System.Reflection;
    using BovineLabs.Core.Extensions;
    using Unity.Entities;
    using UnityEngine;

    public readonly unsafe struct ComponentSaveState
    {
        private const int MaxSaveIgnore = 4;

        internal readonly ulong StableTypeHash;
        internal readonly SaveFeature Feature;
        internal readonly int SaveIgnoreLength;

        // Don't want to use fixed to enforce readonly
#pragma warning disable CS0414 // Field is assigned but its value is never used
        private readonly byte saveIgnore0;
        private readonly byte saveIgnore1;
        private readonly byte saveIgnore2;
        private readonly byte saveIgnore3;
#pragma warning restore CS0414 // Field is assigned but its value is never used

        public ComponentSaveState(ComponentType component, SaveFeature feature = SaveFeature.None, params string[] saveIgnore)
        {
            this.StableTypeHash = TypeManager.GetTypeInfo(component.TypeIndex).StableTypeHash;
            this.Feature = feature;

            this.saveIgnore0 = 0;
            this.saveIgnore1 = 0;
            this.saveIgnore2 = 0;
            this.saveIgnore3 = 0;

            if (saveIgnore == null)
            {
                this.SaveIgnoreLength = 0;
                return;
            }

            if (saveIgnore.Length >= MaxSaveIgnore)
            {
                throw new ArgumentException($"Can only have max of {MaxSaveIgnore} components");
            }

            var type = TypeManager.GetType(component.TypeIndex);
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            fixed (byte* ignores = &this.saveIgnore0)
            {
                var output = 0;
                foreach (var ignore in saveIgnore)
                {
                    var index = fields.IndexOf(f => f.Name == ignore);
                    if (index == -1)
                    {
                        Debug.LogError($"SaveIgnore field {ignore} was not found on type {type}");
                        continue;
                    }

                    ignores[output++] = (byte)index;
                }

                this.SaveIgnoreLength = output;
            }
        }

        internal bool IsIgnored(byte index)
        {
            fixed (byte* ignore = &this.saveIgnore0)
            {
                // Check.Assume(index is >= 0 and < MaxSaveIgnore);
                for (var i = 0; i < this.SaveIgnoreLength; i++)
                {
                    if (ignore[i] == index)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
