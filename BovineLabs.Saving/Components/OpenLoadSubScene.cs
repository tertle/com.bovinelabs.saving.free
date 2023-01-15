// <copyright file="OpenLoadSubScene.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using Unity.Entities;

    public struct OpenLoadSubScene : IComponentData
    {
        public Entity SubScene;
    }
}
