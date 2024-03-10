// <copyright file="SaveCloseSubScene.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Common
{
    using JetBrains.Annotations;
    using Unity.Entities;

    public struct SaveCloseSubScene : IComponentData
    {
        [UsedImplicitly] // Used via Reinterpret
        public Entity SubScene;
    }
}
