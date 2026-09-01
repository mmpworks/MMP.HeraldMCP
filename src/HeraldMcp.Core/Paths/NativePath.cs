// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HeraldMcp.Core.Paths;

/// <summary>
/// The handle-to-final-path resolution A14 requires. On Windows this is
/// GetFinalPathNameByHandle, which resolves symlinks, junctions, 8.3
/// aliases, and volume-GUID forms to one canonical path; the returned
/// \\?\ form is normalized before the root compare. On Unix the canonical
/// path comes from the /proc/self/fd readlink of the open descriptor,
/// which likewise reflects the actual opened object, not the request.
/// </summary>
internal static class NativePath
{
    /// <summary>
    /// Returns the canonical filesystem path of an already-opened handle —
    /// the object actually opened, closing the TOCTOU gap between a
    /// pre-open path check and the open itself.
    /// </summary>
    public static string GetFinalPath(SafeFileHandle handle)
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? GetFinalPathWindows(handle)
            : GetFinalPathUnix(handle);
    }

    private static string GetFinalPathWindows(SafeFileHandle handle)
    {
        const uint FILE_NAME_NORMALIZED = 0x0;
        var needed = GetFinalPathNameByHandleW(handle, null, 0, FILE_NAME_NORMALIZED);
        if (needed == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new char[needed];
        var written = GetFinalPathNameByHandleW(handle, buffer, needed, FILE_NAME_NORMALIZED);
        if (written == 0 || written >= needed)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return StripExtendedPrefix(new string(buffer, 0, (int)written));
    }

    private static string GetFinalPathUnix(SafeFileHandle handle)
    {
        var fd = handle.DangerousGetHandle().ToInt32();
        var linkPath = $"/proc/self/fd/{fd}";
        if (File.Exists(linkPath) || Directory.Exists(linkPath))
        {
            var target = new FileInfo(linkPath).LinkTarget ?? new DirectoryInfo(linkPath).LinkTarget;
            if (target is not null) return target;
        }
        // Fallback: resolve the readlink directly.
        return Path.GetFullPath(linkPath);
    }

    private static string StripExtendedPrefix(string path)
    {
        // \\?\C:\x -> C:\x ; \\?\UNC\server\share -> \\server\share
        const string uncPrefix = @"\\?\UNC\";
        const string devPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.Ordinal))
            return @"\\" + path[uncPrefix.Length..];
        if (path.StartsWith(devPrefix, StringComparison.Ordinal))
            return path[devPrefix.Length..];
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile, char[]? lpszFilePath, uint cchFilePath, uint dwFlags);
}
