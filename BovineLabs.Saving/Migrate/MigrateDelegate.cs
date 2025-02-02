// <copyright file="MigrateDelegate.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Migrate
{
    using BovineLabs.Core.Utility;
    using Unity.Entities;

    public delegate bool MigrateDelegate(ref SystemState systemState, ref Deserializer deserializer);
}
