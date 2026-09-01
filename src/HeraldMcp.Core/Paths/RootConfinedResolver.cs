// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using Microsoft.Win32.SafeHandles;

namespace HeraldMcp.Core.Paths;

/// <summary>
/// Confines every file access to one or more configured roots (PRD section
/// 7.3, anchor A14). This is NEW code: Herald's ConfinedPathResolver is a
/// lexical prefix check, which a symlink planted inside a root defeats.
/// Here the file is OPENED first, then its canonical path is resolved FROM
/// THE OPENED HANDLE and re-checked against the roots, so the validated
/// path is the read path (the TOCTOU property). Discovery uses the same
/// perimeter: enumeration does not follow reparse points, and no file's
/// metadata is surfaced or summed unless it passes the confinement check.
/// </summary>
public sealed class RootConfinedResolver
{
    private const string OutOfRoot =
        "The requested source is outside the configured log roots; ask the operator to add its directory as a root.";

    private readonly string[] _canonicalRoots;

    public RootConfinedResolver(params string[] roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Length == 0)
            throw new ArgumentException("At least one root is required.", nameof(roots));
        _canonicalRoots = new string[roots.Length];
        for (var i = 0; i < roots.Length; i++)
            _canonicalRoots[i] = NormalizeRoot(Path.GetFullPath(roots[i]));
    }

    /// <summary>
    /// Opens a file for read and confirms — from the opened handle — that
    /// it lies inside a configured root. Throws
    /// <see cref="PathConfinementException"/> if not, after closing the
    /// handle. Files are opened with read + write + delete sharing so the
    /// reader never blocks Herald's append or prune (PRD section 7.2).
    /// </summary>
    public SafeFileHandle OpenConfined(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new ArgumentException("Path is empty.", nameof(requestedPath));

        // Fast lexical reject keeps obvious escapes from even opening.
        var lexical = Path.GetFullPath(requestedPath);
        if (!IsInsideAnyRoot(lexical) && !MightResolveInsideRoot(lexical))
            throw new PathConfinementException(OutOfRoot);

        if (!File.Exists(lexical))
            throw new FileNotFoundException("No log file at the requested source.", lexical);

        var handle = File.OpenHandle(
            lexical,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        try
        {
            var finalPath = NormalizeRoot(NativePath.GetFinalPath(handle));
            // finalPath is a file; strip to compare, then confirm containment.
            if (!IsInsideAnyRoot(finalPath))
                throw new PathConfinementException(OutOfRoot);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Enumerates files inside the roots WITHOUT following reparse points,
    /// so a directory symlink or junction pointing out contributes nothing
    /// (A14 discovery clause).
    /// </summary>
    public IEnumerable<string> EnumerateConfinedFiles()
    {
        foreach (var root in _canonicalRoots)
            foreach (var file in EnumerateNoReparse(root))
                yield return file;
    }

    /// <summary>Sums the byte sizes of the confined files (the PRD section 4 ceiling input).</summary>
    public long SumConfinedBytes()
    {
        long total = 0;
        foreach (var file in EnumerateConfinedFiles())
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { /* a file that vanished mid-scan contributes nothing */ }
        }
        return total;
    }

    private static IEnumerable<string> EnumerateNoReparse(string dir)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch (DirectoryNotFoundException) { yield break; }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                var info = new DirectoryInfo(entry);
                if (info.LinkTarget is not null) continue; // reparse point: do not traverse
                foreach (var nested in EnumerateNoReparse(entry))
                    yield return nested;
            }
            else
            {
                var fi = new FileInfo(entry);
                if (fi.LinkTarget is not null) continue; // file symlink: skip, its target may be out of root
                yield return entry;
            }
        }
    }

    private bool IsInsideAnyRoot(string canonicalPath)
    {
        foreach (var root in _canonicalRoots)
        {
            if (canonicalPath.Length >= root.Length
                && canonicalPath.StartsWith(root, PathComparison)
                && (canonicalPath.Length == root.Length
                    || canonicalPath[root.Length] == Path.DirectorySeparatorChar))
            {
                return true;
            }
        }
        return false;
    }

    // A path that is lexically inside a root but not yet resolved may still
    // resolve inside (the common case); allow the open so the handle check
    // decides. A path lexically outside every root cannot resolve inside by
    // opening it, so it is rejected before any open.
    private bool MightResolveInsideRoot(string lexicalPath) => IsInsideAnyRoot(lexicalPath);

    private static string NormalizeRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
