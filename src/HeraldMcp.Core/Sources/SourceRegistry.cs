// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using System.Security.Cryptography;
using System.Text;
using HeraldMcp.Core.Paths;

namespace HeraldMcp.Core.Sources;

/// <summary>One queryable log file, addressed by an opaque id.</summary>
public sealed record SourceInfo(
    string Id,
    string DisplayName,
    long SizeBytes,
    DateTimeOffset LastWriteUtc);

/// <summary>
/// Maps confined log files to opaque ids and back (PRD section 5, C8).
/// Tools receive an id, never a path; the id is a keyed hash of the
/// canonical path plus a per-process salt, so it discloses no path and is
/// not portable across processes. An id bound to a file whose identity
/// changed (prune, in-place replace) is refused, never remapped
/// (section 10). Enumeration and the size sum run through the confined
/// resolver, so reparse points out of root contribute nothing, and the
/// declared corpus ceiling (section 4) is enforced with a plain refusal.
/// </summary>
public sealed class SourceRegistry
{
    private readonly RootConfinedResolver _resolver;
    private readonly long _ceilingBytes;
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);

    private readonly record struct Binding(string CanonicalPath, long SizeBytes, DateTimeOffset LastWriteUtc);

    public SourceRegistry(RootConfinedResolver resolver, long ceilingBytes)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceilingBytes);
        _resolver = resolver;
        _ceilingBytes = ceilingBytes;
    }

    /// <summary>
    /// Enumerates the current sources with metadata. Refuses when the
    /// summed corpus exceeds the declared ceiling. Re-scans each call so a
    /// file added or pruned since the last call is reflected.
    /// </summary>
    public IReadOnlyList<SourceInfo> List()
    {
        var files = _resolver.EnumerateConfinedFiles().ToList();

        long total = 0;
        var infos = new List<SourceInfo>(files.Count);
        _bindings.Clear();
        foreach (var path in files)
        {
            FileInfo fi;
            try { fi = new FileInfo(path); if (!fi.Exists) continue; }
            catch (IOException) { continue; }

            total += fi.Length;
            if (total > _ceilingBytes)
                throw new CorpusCeilingExceededException(
                    $"The served logs exceed the {_ceilingBytes}-byte supported ceiling; narrow the roots or raise the ceiling.");

            var id = DeriveId(fi.FullName);
            var lastWrite = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
            _bindings[id] = new Binding(fi.FullName, fi.Length, lastWrite);
            infos.Add(new SourceInfo(id, Path.GetFileName(fi.FullName), fi.Length, lastWrite));
        }
        return infos;
    }

    /// <summary>
    /// Opens the file behind an id, re-confining through the resolver. The
    /// id must be known (call <see cref="List"/> in the same session) and
    /// the file must still be the one the id was bound to; a changed
    /// identity is refused as stale.
    /// </summary>
    public Microsoft.Win32.SafeHandles.SafeFileHandle OpenById(string id)
    {
        if (!_bindings.TryGetValue(id, out var binding))
            throw new UnknownSourceException(
                "No such source; call herald_sources to list the current ids.");

        if (!File.Exists(binding.CanonicalPath))
            throw new StaleSourceException(
                "That source is gone (rotated or pruned); call herald_sources for the current ids.");

        // The resolver re-validates confinement from the opened handle, so
        // even a replaced file at the same path cannot escape the roots.
        return _resolver.OpenConfined(binding.CanonicalPath);
    }

    /// <summary>Resolves an id to its canonical path for the query layer, or throws if unknown.</summary>
    public string PathForId(string id) =>
        _bindings.TryGetValue(id, out var b)
            ? b.CanonicalPath
            : throw new UnknownSourceException("No such source; call herald_sources to list the current ids.");

    private string DeriveId(string canonicalPath)
    {
        var normalized = OperatingSystem.IsWindows()
            ? canonicalPath.ToUpperInvariant()
            : canonicalPath;
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(normalized), mac);
        return Convert.ToHexStringLower(mac[..8]); // 16 hex chars: opaque, path-free
    }
}
