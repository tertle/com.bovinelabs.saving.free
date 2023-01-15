﻿// <copyright file="Load.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using Unity.Entities;

    [InternalBufferCapacity(0)]
    public struct Load : IBufferElementData
    {
        public byte Value;
    }
}
