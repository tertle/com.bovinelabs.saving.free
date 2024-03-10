// <copyright file="SavableLinks.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using Unity.Entities;

    [InternalBufferCapacity(0)]
    public struct SavableLinks : IBufferElementData
    {
        public Entity Entity;
        public ulong LinkID;
    }
}
