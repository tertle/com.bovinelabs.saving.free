// <copyright file="MigrateAttribute.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving.Migrate
{
    using System;

    public class MigrateAttribute : Attribute
    {
        public readonly ulong From;

        public MigrateAttribute(ulong from)
        {
            this.From = from;
        }
    }
}
