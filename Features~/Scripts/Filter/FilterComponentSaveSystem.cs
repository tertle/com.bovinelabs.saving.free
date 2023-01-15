// <copyright file="FilterComponentSaveSystem.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Filter
{
    using Unity.Entities;

    [WriteGroup(typeof(Load))]
    [WriteGroup(typeof(Save))]
    [WriteGroup(typeof(SaveCloseSubScene))]
    [WriteGroup(typeof(OpenLoadSubScene))]
    public struct FilterComponentSave : IComponentData
    {
    }

    [RequireMatchingQueriesForUpdate]
    public partial class FilterComponentSaveSystem : SaveBaseSystem
    {
        /// <inheritdoc/>
        protected override ComponentType SaveType => ComponentType.ReadOnly<FilterComponentSave>();

        /// <inheritdoc/>
        protected override SaveBuilder CreateSaveBuilder(SaveBuilder saveBuilder)
        {
            return saveBuilder.WithAll<FilterComponent>();
        }
    }
}
