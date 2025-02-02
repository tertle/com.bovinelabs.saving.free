// <copyright file="LoadBuffer.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Data
{
    using Unity.Entities;

    [InternalBufferCapacity(0)]
    public struct LoadBuffer : IBufferElementData
    {
        public byte Value;
    }
}