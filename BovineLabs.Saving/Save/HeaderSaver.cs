// <copyright file="HeaderSaver.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    /// <summary> Header that stores the meta data required of a save file. </summary>
    public unsafe struct HeaderSaver
    {
        /// <summary> The key for a save. For components this is the StableTypeHash. </summary>
        public ulong Key;

        /// <summary> The length in bytes of the entire save file, including this header. </summary>
        public int LengthInBytes;

        /// <summary> Reserved for future. </summary>
        private fixed byte padding[12];
    }
}
