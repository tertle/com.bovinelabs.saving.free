// <copyright file="RollbackSaveSystem.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Rollback
{
    using Unity.Entities;
    using Unity.Transforms;

    [RequireMatchingQueriesForUpdate]
    public partial class RollbackSaveSystem : SaveBaseSystem
    {
        /// <inheritdoc/>
        protected override ComponentType SaveType => ComponentType.ReadOnly<RollbackSave>();

        /// <inheritdoc/>
        protected override SaveBuilder CreateSaveBuilder(SaveBuilder saveBuilder)
        {
            return saveBuilder
                .WithComponentSaver<LocalToWorld>() // Boids sample writes direct to LocalToWorld so let's save that
                .ApplyToExistingPrefabInstances() // Write to existing entities instead of destroying and creating new
                .DoNotCompress(); // For performance
        }
    }
}
