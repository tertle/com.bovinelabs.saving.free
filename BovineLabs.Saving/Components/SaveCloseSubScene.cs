// <copyright file="SaveCloseSubScene.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using Unity.Entities;

    public struct SaveCloseSubScene : IComponentData
    {
        public Entity SubScene;
    }
}
