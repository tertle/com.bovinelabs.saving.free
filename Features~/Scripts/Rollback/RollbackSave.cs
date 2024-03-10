// <copyright file="RollbackSave.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Rollback
{
    using BovineLabs.Saving.Samples.Common;
    using Unity.Entities;

    [WriteGroup(typeof(Load))]
    [WriteGroup(typeof(Save))]
    [WriteGroup(typeof(SaveCloseSubScene))]
    [WriteGroup(typeof(OpenLoadSubScene))]
    public struct RollbackSave : IComponentData
    {
    }
}
