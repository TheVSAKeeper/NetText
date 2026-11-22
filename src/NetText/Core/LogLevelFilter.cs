using System.Buffers;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetText.Core;

internal readonly record struct FilterCursor(long Offset, LogLevel ContinuedLevel = LogLevel.Other);

internal readonly record struct FilterRow(
    string Text,
    LogLevel Level,
    bool Continuation,
    long StartOffset = 0,
    long EndOffset = 0);

internal sealed record FilterPage(List<FilterRow> Rows, FilterCursor? Next);

internal sealed record FilteredEditorProjection(string Text, IReadOnlyList<int> SourceLines)
{
    public bool TryGetSourceLine(int projectedLineIndex, out int originalLineNumber)
    {
        if ((uint)projectedLineIndex < (uint)SourceLines.Count)
        {
            originalLineNumber = SourceLines[projectedLineIndex];
            return true;
        }
        originalLineNumber = 0;
        return false;
    }
}

internal static class LogLevelFilter
{
    public const int ChunkBytes = 32 * 1024;
    public const int MaxPageRows = 100;
    public const int MaxPageBytes = 128 * 1024;

    public static FilterPage ReadFile(
        string path,
        FilterCursor start,
        LogLevel levels,
        FileStamp? expectedStamp = null,
        TextEncoding? encodingOverride = null,
        LogObservation? expectedObservation = null,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        token.ThrowIfCancellationRequested();

        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.SequentialScan);

        var now = LogFile.StampOf(path, handle);
        ulong effectiveLength = now.Length;

        if (expectedStamp is not null && !now.Equals(expectedStamp))
        {
            bool validAppend = expectedObservation is not null &&
                now.Identity.Equals(expectedStamp.Identity) &&
                now.Length > expectedStamp.Length &&
                MatchesAppendPrefix(handle, expectedObservation);
            if (!validAppend)
            {
                throw new IOException("Файл изменился. Закройте фильтр и повторите попытку.");
            }
            effectiveLength = expectedStamp.Length;
        }

        if (levels == LogLevel.None || effectiveLength == 0)
        {
            return new FilterPage([], null);
        }

        TextEncoding encoding = ResolveEncoding(handle, effectiveLength, encodingOverride);
        int unit = TextFormat.UnitSize(encoding);
        int bomLen = DetectActualBomLength(handle, effectiveLength, encoding);

        ulong cursor = (ulong)Math.Clamp(start.Offset, 0, (long)effectiveLength);
        cursor = TextFormat.AlignOffset(cursor, encoding);
        if (cursor < (ulong)bomLen)
        {
            cursor = (ulong)bomLen;
        }

        if (cursor >= effectiveLength)
        {
            return new FilterPage([], null);
        }

        bool prevTerminated = cursor <= (ulong)bomLen || IsLineBoundary(handle, cursor, encoding);
        LogLevel currentLevel = start.ContinuedLevel;

        int maxRowBytes = Math.Min(LogIo.DefaultRowBytes, ChunkBytes);
        int bufferSize = ChunkBytes + unit * 2;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var rows = new List<FilterRow>();
        int totalChars = 0;

        try
        {
            ulong bufFileOffset = cursor;
            int pos = 0;
            int bufferedLen = 0;

            while (cursor < effectiveLength)
            {
                token.ThrowIfCancellationRequested();

                int available = bufferedLen - pos;
                if (available == 0 || (available < maxRowBytes && bufFileOffset + (ulong)bufferedLen < effectiveLength && pos > 0))
                {
                    if (available > 0 && pos > 0)
                    {
                        Buffer.BlockCopy(buffer, pos, buffer, 0, available);
                    }
                    bufFileOffset += (ulong)pos;
                    pos = 0;
                    bufferedLen = available;

                    ulong readOffset = bufFileOffset + (ulong)bufferedLen;
                    if (readOffset < effectiveLength)
                    {
                        int want = (int)Math.Min((ulong)(ChunkBytes - bufferedLen), effectiveLength - readOffset);
                        if (want > 0)
                        {
                            int got = LogIo.ReadAt(handle, readOffset, buffer.AsSpan(bufferedLen, want));
                            bufferedLen += got;
                        }
                    }
                }

                available = bufferedLen - pos;
                if (available <= 0)
                {
                    break;
                }

                int searchLimit = Math.Min(available, maxRowBytes);
                bool foundDelim = LogIo.TryFindNextDelimiter(buffer.AsSpan(0, pos + searchLimit), pos, encoding, out int dStart, out int dLen) &&
                    dStart - pos <= searchLimit;

                // Ensure CRLF split across chunk or search boundary is consumed together.
                if (foundDelim && dStart + unit == pos + searchLimit &&
                    LogIo.IsCarriageReturnUnit(buffer.AsSpan(dStart, unit), encoding))
                {
                    int needed = dStart + unit * 2;
                    if (needed > bufferedLen && bufFileOffset + (ulong)bufferedLen < effectiveLength && needed <= buffer.Length)
                    {
                        int extra = LogIo.ReadAt(handle, bufFileOffset + (ulong)bufferedLen, buffer.AsSpan(bufferedLen, unit));
                        bufferedLen += extra;
                    }
                    if (bufferedLen >= needed && LogIo.IsNewlineUnit(buffer.AsSpan(dStart + unit, unit), encoding))
                    {
                        dLen = unit * 2;
                    }
                }

                // If a CR landed right at the end of a non-full buffer before EOF, check one unit ahead.
                if (foundDelim && dStart + unit == bufferedLen &&
                    bufFileOffset + (ulong)bufferedLen < effectiveLength &&
                    bufferedLen + unit <= buffer.Length &&
                    LogIo.IsCarriageReturnUnit(buffer.AsSpan(dStart, unit), encoding))
                {
                    int extra = LogIo.ReadAt(handle, bufFileOffset + (ulong)bufferedLen, buffer.AsSpan(bufferedLen, unit));
                    bufferedLen += extra;
                    if (bufferedLen >= dStart + unit * 2 && LogIo.IsNewlineUnit(buffer.AsSpan(dStart + unit, unit), encoding))
                    {
                        dLen = unit * 2;
                    }
                }

                ulong rowStart = cursor;
                int contentBytes;
                int consumedBytes;
                bool terminated;

                if (foundDelim)
                {
                    contentBytes = dStart - pos;
                    consumedBytes = contentBytes + dLen;
                    terminated = true;
                }
                else if (bufFileOffset + (ulong)bufferedLen < effectiveLength || available > maxRowBytes)
                {
                    int len = searchLimit;
                    if (unit == 2 && (len % 2) != 0)
                    {
                        len--;
                    }
                    else if (encoding is TextEncoding.Utf8 or TextEncoding.Utf8Bom)
                    {
                        len = LogIo.TrimUtf8Boundary(buffer.AsSpan(pos, len));
                    }
                    if (len == 0)
                    {
                        len = Math.Min(available, searchLimit);
                    }
                    contentBytes = len;
                    consumedBytes = len;
                    terminated = false;
                }
                else
                {
                    int len = unit == 2 && (available % 2) != 0 ? available - 1 : available;
                    contentBytes = len;
                    consumedBytes = available;
                    terminated = true;
                }

                if (consumedBytes <= 0)
                {
                    break;
                }

                string text = TextFormat.DecodeLossy(buffer.AsSpan(pos, contentBytes), encoding, atFileStart: false);
                ulong rowEnd = rowStart + (ulong)consumedBytes;
                bool continuation = !prevTerminated;
                prevTerminated = terminated;

                if (!continuation)
                {
                    currentLevel = LogLevels.Classify(text);
                }

                pos += consumedBytes;
                cursor = rowEnd;

                if (continuation && terminated && text.Length == 0)
                {
                    continue;
                }

                if ((levels & currentLevel) == 0)
                {
                    continue;
                }

                if (rows.Count >= MaxPageRows || (rows.Count > 0 && totalChars >= MaxPageBytes))
                {
                    return new FilterPage(rows, new FilterCursor((long)rowStart, currentLevel));
                }

                rows.Add(new FilterRow(text, currentLevel, continuation, (long)rowStart, (long)rowEnd));
                totalChars += text.Length;
            }

            return new FilterPage(rows, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static TextEncoding ResolveEncoding(SafeFileHandle handle, ulong fileLength, TextEncoding? overrideEncoding)
    {
        if (overrideEncoding is TextEncoding ov && ov != TextEncoding.None)
        {
            return ov;
        }
        Span<byte> prefix = stackalloc byte[3];
        int n = fileLength > 0 ? LogIo.ReadAt(handle, 0, prefix) : 0;
        return TextFormat.DetectStreamEncoding(prefix[..n]);
    }

    private static int DetectActualBomLength(SafeFileHandle handle, ulong fileLength, TextEncoding encoding)
    {
        int expected = TextFormat.BomLength(encoding);
        if (expected == 0 || fileLength < (ulong)expected)
        {
            return 0;
        }
        Span<byte> prefix = stackalloc byte[3];
        int read = LogIo.ReadAt(handle, 0, prefix[..expected]);
        if (read < expected) return 0;
        return encoding switch
        {
            TextEncoding.Utf8Bom when prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF => 3,
            TextEncoding.Utf16Le when prefix[0] == 0xFF && prefix[1] == 0xFE => 2,
            TextEncoding.Utf16Be when prefix[0] == 0xFE && prefix[1] == 0xFF => 2,
            _ => 0,
        };
    }

    private static bool MatchesAppendPrefix(SafeFileHandle handle, LogObservation previous)
    {
        Span<byte> witness = stackalloc byte[LogFile.WitnessBytes];
        if (previous.Head.Length > 0)
        {
            int headRead = LogIo.ReadAt(handle, 0, witness[..previous.Head.Length]);
            if (headRead != previous.Head.Length || !witness[..headRead].SequenceEqual(previous.Head))
            {
                return false;
            }
        }
        if (previous.Tail.Length > 0)
        {
            ulong tailOffset = previous.Stamp.Length - (ulong)previous.Tail.Length;
            int tailRead = LogIo.ReadAt(handle, tailOffset, witness[..previous.Tail.Length]);
            if (tailRead != previous.Tail.Length || !witness[..tailRead].SequenceEqual(previous.Tail))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLineBoundary(SafeFileHandle handle, ulong offset, TextEncoding encoding)
    {
        int unit = TextFormat.UnitSize(encoding);
        if (offset < (ulong)unit)
        {
            return true;
        }

        Span<byte> prev = stackalloc byte[2];
        if (LogIo.ReadAt(handle, offset - (ulong)unit, prev[..unit]) < unit)
        {
            return false;
        }

        if (LogIo.IsNewlineUnit(prev[..unit], encoding))
        {
            return true;
        }

        if (!LogIo.IsCarriageReturnUnit(prev[..unit], encoding))
        {
            return false;
        }

        Span<byte> next = stackalloc byte[2];
        int n = LogIo.ReadAt(handle, offset, next[..unit]);
        return n < unit || !LogIo.IsNewlineUnit(next[..unit], encoding);
    }
}

internal static class EditorLogFilter
{
    public static FilteredEditorProjection Filter(string text, LogLevel levels, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (levels == LogLevel.None || text.Length == 0)
        {
            return new FilteredEditorProjection(string.Empty, []);
        }

        var result = new StringBuilder();
        var sourceLines = new List<int>();
        int lineNumber = 0;
        int pos = 0;

        while (pos < text.Length)
        {
            token.ThrowIfCancellationRequested();
            lineNumber++;
            int rel = text.AsSpan(pos).IndexOfAny('\r', '\n');
            int end = rel < 0 ? text.Length : pos + rel;
            string line = text[pos..end];

            if (rel < 0)
            {
                pos = text.Length;
            }
            else if (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n')
            {
                pos = end + 2;
            }
            else
            {
                pos = end + 1;
            }

            if ((levels & LogLevels.Classify(line)) == 0) continue;
            if (sourceLines.Count > 0) result.Append('\n');
            result.Append(line);
            sourceLines.Add(lineNumber);
        }

        return new FilteredEditorProjection(result.ToString(), sourceLines);
    }

    public static FilteredEditorProjection Filter(TextReader reader, LogLevel levels, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (levels == LogLevel.None)
        {
            return new FilteredEditorProjection(string.Empty, []);
        }

        var result = new StringBuilder();
        var sourceLines = new List<int>();
        string? line;
        int lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            token.ThrowIfCancellationRequested();
            lineNumber++;
            if ((levels & LogLevels.Classify(line)) == 0) continue;
            if (sourceLines.Count > 0) result.Append('\n');
            result.Append(line);
            sourceLines.Add(lineNumber);
        }

        return new FilteredEditorProjection(result.ToString(), sourceLines);
    }
}
