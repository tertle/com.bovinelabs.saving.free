// <copyright file="SaveSystem.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using Unity.Entities;

    /// <summary> The default save system. Unless custom <see cref="ISaver"/> are required, this system should handle saving and loading by default. </summary>
    public partial class SaveSystem : SaveBaseSystem
    {
        /// <inheritdoc/>
        protected override ComponentType SaveType => default;
    }
}
