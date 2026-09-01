// SPDX-License-Identifier: Apache-2.0
// Copyright (c) MMPWorks. See LICENSE for terms.
namespace HeraldMcp.Core.Reading;

/// <summary>
/// The read path for every log scan (PRD section 7.8, anchor A15). Wraps a
/// caller-owned <see cref="TextReader"/> and enforces a hard per-line
/// length cap. An over-length line is discarded through its next newline
/// and counted in <see cref="SkippedOverlongLines"/> as ONE line, so a
/// hostile newline-free line can neither exhaust memory nor re-enter the
/// stream as spurious lines. Terminator handling matches
/// <see cref="TextReader.ReadLine"/>: '\n', "\r\n", and lone '\r'.
/// The underlying reader is never disposed here; the caller that opened
/// the handle owns its lifetime.
/// </summary>
public sealed class BoundedLineReader : IDisposable
{
    /// <summary>Fill-buffer size; exposed so tests can pin boundary cases.</summary>
    public const int InternalBufferSize = 4096;

    /// <summary>Production default line cap: 1 MiB of characters (PRD A15).</summary>
    public const int DefaultMaxLineLength = 1024 * 1024;

    private readonly TextReader _inner;
    private readonly int _maxLineLength;
    private readonly char[] _buffer = new char[InternalBufferSize];
    private int _bufferLength;
    private int _position;
    private bool _pendingLfSkip; // last char of the previous fill was '\r'

    public BoundedLineReader(TextReader inner, int maxLineLength = DefaultMaxLineLength)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineLength, 1);
        _inner = inner;
        _maxLineLength = maxLineLength;
    }

    /// <summary>Count of over-cap lines discarded so far.</summary>
    public int SkippedOverlongLines { get; private set; }

    /// <summary>
    /// Returns the next line whose length is within the cap, or null at
    /// end of input. Over-cap lines are consumed, counted, and skipped.
    /// </summary>
    public string? ReadLine()
    {
        while (true)
        {
            var line = ReadRawLine();
            if (line is null) return null;
            if (line.Value.Overflowed)
            {
                SkippedOverlongLines++;
                continue;
            }
            return line.Value.Text;
        }
    }

    private readonly record struct RawLine(string? Text, bool Overflowed);

    /// <summary>
    /// Reads one physical line. When the accumulated length passes the cap
    /// the rest of the line is consumed WITHOUT being stored, so memory
    /// stays bounded by the cap regardless of input size.
    /// </summary>
    private RawLine? ReadRawLine()
    {
        System.Text.StringBuilder? sb = null;
        var length = 0;
        var overflowed = false;
        var sawAnything = false;

        while (true)
        {
            if (_position >= _bufferLength)
            {
                _bufferLength = _inner.Read(_buffer, 0, _buffer.Length);
                _position = 0;
                if (_bufferLength <= 0)
                {
                    if (!sawAnything) return null;
                    return new RawLine(overflowed ? null : (sb?.ToString() ?? string.Empty), overflowed);
                }
            }

            if (_pendingLfSkip)
            {
                _pendingLfSkip = false;
                if (_buffer[_position] == '\n')
                {
                    _position++;
                    continue;
                }
            }

            sawAnything = true;
            var start = _position;
            while (_position < _bufferLength)
            {
                var c = _buffer[_position];
                if (c is '\n' or '\r')
                {
                    var runLength = _position - start;
                    AppendBounded(ref sb, ref length, ref overflowed, start, runLength);
                    _position++;
                    if (c == '\r')
                    {
                        if (_position < _bufferLength)
                        {
                            if (_buffer[_position] == '\n') _position++;
                        }
                        else
                        {
                            _pendingLfSkip = true;
                        }
                    }
                    return new RawLine(overflowed ? null : (sb?.ToString() ?? string.Empty), overflowed);
                }
                _position++;
            }

            AppendBounded(ref sb, ref length, ref overflowed, start, _position - start);
        }
    }

    private void AppendBounded(
        ref System.Text.StringBuilder? sb, ref int length, ref bool overflowed,
        int start, int runLength)
    {
        if (runLength <= 0) return;
        length += runLength;
        if (overflowed) return;
        if (length > _maxLineLength)
        {
            overflowed = true;
            sb = null; // release what was accumulated; the line is discarded
            return;
        }
        sb ??= new System.Text.StringBuilder(Math.Min(_maxLineLength, runLength + 16));
        sb.Append(_buffer, start, runLength);
    }

    /// <summary>Releases only this wrapper. The caller owns the inner reader.</summary>
    public void Dispose()
    {
        // Nothing of ours to release; the buffer is managed memory and the
        // inner reader belongs to the caller (PRD 7.8 / PR #6 contract).
    }
}
