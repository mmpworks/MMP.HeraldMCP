// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
using HeraldMcp.Core.Reading;

namespace HeraldMcp.Tests.Reading;

/// <summary>
/// A15 (PRD section 7.8): the bounded reader is the read path. An
/// over-length line is discarded through its next newline and counts as
/// ONE skipped line; its tail can never re-enter as spurious lines.
/// </summary>
public sealed class BoundedLineReaderTests
{
    private const int Cap = 64; // small cap so tests stay fast; production default is 1 MiB

    private static BoundedLineReader Make(string content, int cap = Cap) =>
        new(new StringReader(content), cap);

    private static List<string> Drain(BoundedLineReader reader)
    {
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private static List<string> Reference(string content)
    {
        using var sr = new StringReader(content);
        var lines = new List<string>();
        while (sr.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    [Fact]
    public void Matches_StreamReader_semantics_on_ordinary_content()
    {
        const string content = "alpha\nbeta\r\ngamma\rdelta\n\nlast";
        using var reader = Make(content);
        Assert.Equal(Reference(content), Drain(reader));
        Assert.Equal(0, reader.SkippedOverlongLines);
    }

    [Fact]
    public void Overlong_line_is_skipped_whole_and_counted_once()
    {
        var content = new string('x', Cap + 1) + "\nok";
        using var reader = Make(content);
        Assert.Equal(new[] { "ok" }, Drain(reader));
        Assert.Equal(1, reader.SkippedOverlongLines);
    }

    [Fact]
    public void Overlong_tail_never_reenters_as_lines()
    {
        // The discarded region itself contains no newline until the real
        // terminator, so nothing between cap and the newline may surface.
        var content = new string('x', Cap * 40) + "\nsurvivor";
        using var reader = Make(content);
        var lines = Drain(reader);
        Assert.Equal(new[] { "survivor" }, lines);
        Assert.Equal(1, reader.SkippedOverlongLines);
    }

    [Fact]
    public void Line_of_length_at_cap_passes()
    {
        var exact = new string('y', Cap);
        using var reader = Make(exact + "\nnext");
        Assert.Equal(new[] { exact, "next" }, Drain(reader));
        Assert.Equal(0, reader.SkippedOverlongLines);
    }

    [Fact]
    public void Torn_tail_without_newline_is_returned()
    {
        using var reader = Make("full line\npartial");
        Assert.Equal(new[] { "full line", "partial" }, Drain(reader));
    }

    [Fact]
    public void Overlong_line_at_eof_without_newline_is_skipped_and_counted()
    {
        using var reader = Make(new string('z', Cap + 5));
        Assert.Empty(Drain(reader));
        Assert.Equal(1, reader.SkippedOverlongLines);
    }

    [Fact]
    public void Crlf_split_across_internal_buffer_boundary_is_one_terminator()
    {
        // Force \r as the last char of one fill and \n as the first of the
        // next by sizing the line to the reader's internal buffer.
        var line = new string('a', BoundedLineReader.InternalBufferSize - 1);
        using var reader = new BoundedLineReader(
            new StringReader(line + "\r\nnext"), BoundedLineReader.InternalBufferSize + 16);
        Assert.Equal(new[] { line, "next" }, Drain(reader));
    }

    [Fact]
    public void Multiple_overlong_lines_count_individually()
    {
        var over = new string('q', Cap + 1);
        using var reader = Make($"{over}\ngood\n{over}\n{over}\nalso good");
        Assert.Equal(new[] { "good", "also good" }, Drain(reader));
        Assert.Equal(3, reader.SkippedOverlongLines);
    }

    [Fact]
    public void Does_not_dispose_the_underlying_reader()
    {
        var tracking = new DisposeTrackingReader("payload\n");
        var reader = new BoundedLineReader(tracking, Cap);
        reader.Dispose();
        Assert.False(tracking.Disposed);
    }

    [Fact]
    public void Empty_input_yields_no_lines()
    {
        using var reader = Make("");
        Assert.Empty(Drain(reader));
        Assert.Equal(0, reader.SkippedOverlongLines);
    }

    [Fact]
    public void Cap_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BoundedLineReader(new StringReader("x"), 0));
    }

    /// <summary>
    /// Property sweep: for random content mixing line lengths around the
    /// cap, the reader returns THE SAME lines a reference ReadLine returns
    /// minus the over-cap ones, and counts the over-cap ones. Deterministic
    /// seeds so a failure reproduces.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(99991)]
    public void Property_random_content_partitions_into_kept_plus_skipped(int seed)
    {
        var rng = new Random(seed);
        var expectedKept = new List<string>();
        var expectedSkipped = 0;
        var sb = new System.Text.StringBuilder();
        var lineCount = rng.Next(50, 300);
        for (var i = 0; i < lineCount; i++)
        {
            // Length clusters around the cap on purpose: 0..2*Cap.
            var len = rng.Next(0, Cap * 2 + 1);
            var line = string.Create(len, rng, static (span, r) =>
            {
                for (var j = 0; j < span.Length; j++)
                    span[j] = (char)r.Next('a', 'z' + 1);
            });
            if (line.Length > Cap) expectedSkipped++;
            else expectedKept.Add(line);
            sb.Append(line);
            sb.Append(rng.Next(3) switch { 0 => "\n", 1 => "\r\n", _ => "\r" });
        }

        using var reader = Make(sb.ToString());
        Assert.Equal(expectedKept, Drain(reader));
        Assert.Equal(expectedSkipped, reader.SkippedOverlongLines);
    }

    private sealed class DisposeTrackingReader(string content) : TextReader
    {
        private readonly StringReader _inner = new(content);
        public bool Disposed { get; private set; }
        public override int Read(char[] buffer, int index, int count) =>
            _inner.Read(buffer, index, count);
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
