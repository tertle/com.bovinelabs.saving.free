// <copyright file="SubSceneRecord.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using Unity.Entities;

    /// <summary> This record is used to track deleted sub scene objects. </summary>
    [InternalBufferCapacity(0)] // TODO i think we could allocate this
    public struct SubSceneRecord : IBufferElementData
    {
        public SavableScene Savable;
    }
}
