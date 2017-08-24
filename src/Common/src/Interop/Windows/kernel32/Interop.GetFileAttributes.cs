// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Runtime.InteropServices;

internal partial class Interop
{
    internal partial class Kernel32
    {
        /// <summary>
        /// WARNING: This method does not implicitly handle long paths. Use GetFileAttributes.
        /// </summary>
        [DllImport(Libraries.Kernel32, EntryPoint = "GetFileAttributesW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern FileAttributes GetFileAttributesPrivate(string lpFileName);

        internal static FileAttributes GetFileAttributes(string name)
        {
            name = PathInternal.EnsureExtendedPrefixOverMaxPath(name);
            return GetFileAttributesPrivate(name);
        }
    }
}
