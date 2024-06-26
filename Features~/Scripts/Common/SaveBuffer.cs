﻿// <copyright file="SaveBuffer.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Common
{
    using Unity.Entities;

    [InternalBufferCapacity(0)]
    public struct SaveBuffer : IBufferElementData
    {
        private byte value;
    }
}
