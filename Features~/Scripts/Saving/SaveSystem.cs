// <copyright file="SaveSystem.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Samples.Saving
{
    using Unity.Entities;
    using Unity.Rendering;

    /// <summary> The default save system. Unless custom <see cref="ISaver"/> are required, this system should handle saving and loading by default. </summary>
    public partial class SaveSystem : Samples.Saving.SaveBaseSystem
    {
        /// <inheritdoc/>
        protected override ComponentType SaveType => default;

        protected override SaveBuilder CreateSaveBuilder(SaveBuilder saveBuilder)
        {
            // Add URPMaterialPropertyBaseColor for the add remove component demos
            return saveBuilder.WithComponentSaver<URPMaterialPropertyBaseColor>(SaveFeature.AddComponent);
        }
    }
}
