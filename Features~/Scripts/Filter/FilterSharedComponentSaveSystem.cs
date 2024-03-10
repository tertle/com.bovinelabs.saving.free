// <copyright file="FilterSharedComponentSaveSystem.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Filter
{
    using BovineLabs.Saving.Samples.Common;
    using BovineLabs.Saving.Samples.Saving;
    using Unity.Entities;

    [WriteGroup(typeof(Load))]
    [WriteGroup(typeof(Save))]
    [WriteGroup(typeof(SaveCloseSubScene))]
    [WriteGroup(typeof(OpenLoadSubScene))]
    public struct FilterSharedComponentSave : IComponentData
    {
    }

    [RequireMatchingQueriesForUpdate]
    public partial class FilterSharedComponentSaveSystem : SaveBaseSystem
    {
        /// <inheritdoc/>
        protected override ComponentType SaveType => ComponentType.ReadOnly<FilterSharedComponentSave>();

        /// <inheritdoc/>
        protected override SaveBuilder CreateSaveBuilder(SaveBuilder saveBuilder)
        {
            return saveBuilder.WithAll<FilterSharedComponent>();
        }

        /// <inheritdoc/>
        protected override void ManagerCreated(SaveManager saveManager)
        {
            saveManager.SetSharedComponentFilter(new FilterSharedComponent { Value = 1 });
        }
    }
}
