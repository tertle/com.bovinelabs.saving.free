// <copyright file="SaveAttribute.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using System;
    using Unity.Entities;

    /// <summary>
    /// The attribute is used to auto generate a <see cref="ComponentSave"/> for a specific <see cref="IComponentData"/> or <see cref="IBufferElementData"/>.
    /// Adding this to your component will cause the component to be saved. This only works on unmanaged components.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public class SaveAttribute : Attribute
    {
        public SaveAttribute(SaveFeature feature = SaveFeature.None)
        {
            this.Feature = feature;
        }

        public SaveFeature Feature { get; }
    }
}
