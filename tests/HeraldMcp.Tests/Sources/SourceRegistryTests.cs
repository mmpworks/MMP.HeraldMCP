// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using HeraldMcp.Core.Paths;
using HeraldMcp.Core.Sources;

namespace HeraldMcp.Tests.Sources;

/// <summary>
/// Opaque source IDs (PRD section 5, C8) and the corpus ceiling (section 4).
/// Tools take an id, never a path; the id never embeds or discloses the
/// path; an id cannot address outside the configured roots; and an id held
/// across a prune is refused, never silently remapped (section 10).
/// </summary>
public sealed class SourceRegistryTests : IDisposable
{
    private readonly string _root;

    public SourceRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "heraldmcp-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.log"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(_root, "worker.log"), "a\nb\nc\n");
    }

    private SourceRegistry Make(long ceilingBytes = 50L * 1024 * 1024) =>
        new(new RootConfinedResolver(_root), ceilingBytes);

    [Fact]
    public void Ids_are_opaque_and_disclose_no_path()
    {
        var registry = Make();
        foreach (var src in registry.List())
        {
            Assert.DoesNotContain(_root, src.Id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("app.log", src.Id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.DirectorySeparatorChar, src.Id);
            Assert.DoesNotContain(":", src.Id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_confined_file_gets_an_id()
    {
        var registry = Make();
        var names = registry.List().Select(s => s.DisplayName).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "app.log", "worker.log" }, names);
    }

    [Fact]
    public void Id_resolves_back_to_a_confined_handle()
    {
        var registry = Make();
        var id = registry.List().First(s => s.DisplayName == "app.log").Id;
        using var handle = registry.OpenById(id);
        Assert.NotNull(handle);
    }

    [Fact]
    public void Unknown_id_is_refused_with_a_plain_sentence()
    {
        var registry = Make();
        var ex = Assert.Throws<UnknownSourceException>(() => registry.OpenById("deadbeefdeadbeef"));
        Assert.Contains("source", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Id_is_stable_across_two_lists_of_the_same_file()
    {
        var registry = Make();
        var first = registry.List().First(s => s.DisplayName == "app.log").Id;
        var second = registry.List().First(s => s.DisplayName == "app.log").Id;
        Assert.Equal(first, second);
    }

    [Fact]
    public void Id_held_across_a_prune_is_refused_not_remapped()
    {
        var registry = Make();
        var id = registry.List().First(s => s.DisplayName == "app.log").Id;
        File.Delete(Path.Combine(_root, "app.log")); // prune
        var ex = Record.Exception(() => registry.OpenById(id).Dispose());
        Assert.True(ex is StaleSourceException or UnknownSourceException,
            $"a pruned id must be refused, got {ex?.GetType().Name ?? "success"}");
    }

    [Fact]
    public void Metadata_reports_size_and_freshness()
    {
        var registry = Make();
        var src = registry.List().First(s => s.DisplayName == "worker.log");
        Assert.True(src.SizeBytes > 0);
        Assert.True(src.LastWriteUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Over_ceiling_corpus_is_refused_with_a_plain_sentence()
    {
        // Ceiling below the two small files' combined size.
        var registry = Make(ceilingBytes: 1);
        var ex = Assert.Throws<CorpusCeilingExceededException>(() => registry.List());
        Assert.Contains("ceiling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void At_ceiling_corpus_is_allowed()
    {
        var total = new FileInfo(Path.Combine(_root, "app.log")).Length
                  + new FileInfo(Path.Combine(_root, "worker.log")).Length;
        var registry = Make(ceilingBytes: total);
        Assert.Equal(2, registry.List().Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }
}
