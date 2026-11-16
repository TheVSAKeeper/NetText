using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace NetText.Core;

public sealed record LogRow(ulong Start, ulong End, string Text, bool Continues, bool Terminated);

public sealed record LogPage(List<LogRow> Rows, ulong NextOffset, ulong FileSize, bool Eof);

public sealed record PageOptions(int MaxRows, int MaxRowBytes)
{
    public static PageOptions Default => new(500, LogIo.DefaultRowBytes);
}

public readonly record struct SparseLine(ulong Line, ulong Offset);

public sealed class LogCancelledException : OperationCanceledException
{
    public LogCancelledException() : base("Поиск отменён") { }
}

public sealed class LogLineLimitException : IOException
{
    public ulong Offset { get; }
    public int Limit { get; }

    public LogLineLimitException(ulong offset, int limit)
        : base($"Строка на смещении {offset} превышает лимит {limit} байт")
    {
        Offset = offset;
        Limit = limit;
    }
}

public sealed class LineIndex
{
    public const int IndexSampleBytes = 64 * 1024;
    public const int MaxIndexEntries = 131_072;

    private readonly List<SparseLine> _entries = [new(1, 0)];

    public ulong ScannedTo { get; private set; }
    public ulong LineCount { get; private set; } = 1;
    public ulong NextSample { get; private set; } = IndexSampleBytes;
    public ulong SampleStride { get; private set; } = IndexSampleBytes;
    public IReadOnlyList<SparseLine> Entries => _entries;

    public void Reset()
    {
        _entries.Clear();
        _entries.Add(new(1, 0));
        ScannedTo = 0;
        LineCount = 1;
        NextSample = IndexSampleBytes;
        SampleStride = IndexSampleBytes;
    }

    public ulong Advance(SafeFileHandle handle, TextEncoding encoding, ulong through, CancellationToken ct)
    {
        if (through <= ScannedTo)
        {
            return 0;
        }

        int unit = TextFormat.UnitSize(encoding);
        int chunkStep = LogIo.BlockSize * 16;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(unit + chunkStep + unit);
        ulong startScanned = ScannedTo;
        ulong cursor = ScannedTo;
        ulong diskPos = cursor;
        int carryLen = 0;

        try
        {
            bool singleByte = unit == 1;
            while (cursor < through)
            {
                ct.ThrowIfCancellationRequested();

                ulong remaining = through > diskPos ? through - diskPos : 0;
                int wantRead = (int)Math.Min((ulong)chunkStep, remaining);
                int got = wantRead > 0 ? LogIo.ReadAt(handle, diskPos, buffer.AsSpan(carryLen, wantRead)) : 0;
                diskPos += (ulong)got;

                int totalAvail = carryLen + got;
                if (totalAvail == 0)
                {
                    break;
                }

                bool reachedEof = got < wantRead;
                bool moreInBatch = !reachedEof && diskPos < through;
                int advanceBytes;
                int nextCarry = 0;
                int extraLookahead = 0;

                if (moreInBatch)
                {
                    advanceBytes = totalAvail - unit;
                    if (advanceBytes <= 0)
                    {
                        break;
                    }
                    if (!singleByte && (advanceBytes % 2) != 0)
                    {
                        advanceBytes--;
                        if (advanceBytes <= 0)
                        {
                            break;
                        }
                    }
                    nextCarry = totalAvail - advanceBytes;
                }
                else
                {
                    advanceBytes = totalAvail;
                    if (!singleByte && (advanceBytes % 2) != 0)
                    {
                        advanceBytes--;
                        if (advanceBytes <= 0)
                        {
                            break;
                        }
                    }
                    if (!reachedEof)
                    {
                        extraLookahead = LogIo.ReadAt(handle, diskPos, buffer.AsSpan(totalAvail, unit));
                    }
                }

                int validLen = totalAvail + extraLookahead;
                if (singleByte)
                {
                    AdvanceSingleByteBlock(buffer.AsSpan(0, validLen), 0, advanceBytes, cursor);
                }
                else
                {
                    int pos = 0;
                    while (pos < validLen)
                    {
                        if (!LogIo.TryFindNextDelimiter(buffer.AsSpan(0, validLen), pos, encoding, out int dStart, out int dLen))
                        {
                            break;
                        }

                        int delimEnd = dStart + dLen;
                        pos = delimEnd;

                        if (delimEnd > advanceBytes)
                        {
                            continue;
                        }

                        RecordLine(cursor + (ulong)delimEnd);
                    }
                }

                cursor += (ulong)advanceBytes;
                ScannedTo = cursor;

                if (nextCarry > 0)
                {
                    Buffer.BlockCopy(buffer, advanceBytes, buffer, 0, nextCarry);
                }
                carryLen = nextCarry;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return ScannedTo - startScanned;
    }

    private unsafe void AdvanceSingleByteBlock(ReadOnlySpan<byte> block, int startPos, int advanceBytes, ulong startFileOffset)
    {
        int endPos = startPos + advanceBytes;
        int i = startPos;
        ulong lineCount = LineCount;
        ulong nextSample = NextSample;

        fixed (byte* pBase = block)
        {
            if (Vector256.IsHardwareAccelerated)
            {
                var vLf = Vector256.Create((byte)'\n');
                var vCr = Vector256.Create((byte)'\r');
                while (i + 32 <= endPos)
                {
                    var v = Vector256.Load(pBase + i);
                    uint lfMask = Vector256.Equals(v, vLf).ExtractMostSignificantBits();
                    uint crMask = Vector256.Equals(v, vCr).ExtractMostSignificantBits();
                    uint nextIsLf = (i + 32 < block.Length && pBase[i + 32] == (byte)'\n') ? 1U : 0U;
                    uint shiftedLf = (lfMask >> 1) | (nextIsLf << 31);
                    uint endMask = lfMask | (crMask & ~shiftedLf);

                    ulong vecFileStart = startFileOffset + (ulong)(i - startPos);
                    if (vecFileStart + 32 < nextSample)
                    {
                        lineCount += (ulong)BitOperations.PopCount(endMask);
                    }
                    else
                    {
                        while (endMask != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(endMask);
                            LineCount = ++lineCount;
                            RecordSample(vecFileStart + (ulong)bit + 1UL, ref nextSample);
                            endMask &= endMask - 1;
                        }
                    }

                    i += 32;
                }
            }
            else if (Vector128.IsHardwareAccelerated)
            {
                var vLf = Vector128.Create((byte)'\n');
                var vCr = Vector128.Create((byte)'\r');
                while (i + 16 <= endPos)
                {
                    var v = Vector128.Load(pBase + i);
                    uint lfMask = Vector128.Equals(v, vLf).ExtractMostSignificantBits();
                    uint crMask = Vector128.Equals(v, vCr).ExtractMostSignificantBits();
                    uint nextIsLf = (i + 16 < block.Length && pBase[i + 16] == (byte)'\n') ? 1U : 0U;
                    uint shiftedLf = (lfMask >> 1) | (nextIsLf << 15);
                    uint endMask = lfMask | (crMask & ~shiftedLf);

                    ulong vecFileStart = startFileOffset + (ulong)(i - startPos);
                    if (vecFileStart + 16 < nextSample)
                    {
                        lineCount += (ulong)BitOperations.PopCount(endMask);
                    }
                    else
                    {
                        while (endMask != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(endMask);
                            LineCount = ++lineCount;
                            RecordSample(vecFileStart + (ulong)bit + 1UL, ref nextSample);
                            endMask &= endMask - 1;
                        }
                    }

                    i += 16;
                }
            }
        }

        while (i < endPos)
        {
            byte b = block[i];
            if (b == (byte)'\n' || (b == (byte)'\r' && (i + 1 >= block.Length || block[i + 1] != (byte)'\n')))
            {
                lineCount++;
                ulong lineOffset = startFileOffset + (ulong)(i - startPos) + 1UL;
                if (lineOffset >= nextSample)
                {
                    LineCount = lineCount;
                    RecordSample(lineOffset, ref nextSample);
                }
            }
            i++;
        }

        LineCount = lineCount;
        NextSample = nextSample;
    }

    private void RecordSample(ulong lineOffset, ref ulong nextSample)
    {
        if (lineOffset < nextSample) return;
        if (_entries.Count >= MaxIndexEntries)
        {
            ThinEntries();
        }
        _entries.Add(new(LineCount, lineOffset));
        nextSample = lineOffset + SampleStride;
    }

    private void RecordLine(ulong lineOffset)
    {
        LineCount++;
        if (lineOffset >= NextSample)
        {
            ulong next = NextSample;
            RecordSample(lineOffset, ref next);
            NextSample = next;
        }
    }

    public SparseLine FindAnchor(ulong target)
    {
        ulong aligned = TextFormat.AlignOffset(target, TextEncoding.Utf8);
        int idx = FindAnchorIndex(aligned);
        return _entries[idx];
    }

    public ulong LineStartAt(SafeFileHandle handle, TextEncoding encoding, ulong offset, ulong fileSize)
    {
        ulong target = TextFormat.AlignOffset(Math.Min(offset, fileSize), encoding);
        int idx = FindAnchorIndex(target);
        return LineStartAtFromAnchor(handle, encoding, target, fileSize, _entries[idx]);
    }

    public static ulong LineStartAtFromAnchor(SafeFileHandle handle, TextEncoding encoding, ulong offset, ulong fileSize, SparseLine anchor)
    {
        ulong target = TextFormat.AlignOffset(Math.Min(offset, fileSize), encoding);
        ulong anchorOffset = anchor.Offset;
        if (anchorOffset >= target)
        {
            return anchorOffset;
        }

        int unit = TextFormat.UnitSize(encoding);
        int overlap = unit * 2;
        var buffer = ArrayPool<byte>.Shared.Rent(overlap + LogIo.BlockSize + unit);
        ulong cursor = anchorOffset;
        ulong result = anchorOffset;

        try
        {
            while (cursor < target)
            {
                ulong blockStart = cursor > (ulong)overlap ? cursor - (ulong)overlap : 0;
                int skipBefore = (int)(cursor - blockStart);
                int want = (int)Math.Min((ulong)(LogIo.BlockSize + unit), (target - cursor) + (ulong)unit) + skipBefore;
                int read = LogIo.ReadAt(handle, blockStart, buffer.AsSpan(0, want));
                if (read <= skipBefore)
                {
                    break;
                }

                ulong advance = Math.Min((ulong)LogIo.BlockSize, Math.Min(target - cursor, (ulong)(read - skipBefore)));
                int pos = 0;
                while (pos < read)
                {
                    if (!LogIo.TryFindNextDelimiter(buffer.AsSpan(0, read), pos, encoding, out int dStart, out int dLen))
                    {
                        break;
                    }

                    ulong lineOffset = blockStart + (ulong)(dStart + dLen);
                    pos = dStart + dLen;
                    if (lineOffset > cursor && lineOffset <= target)
                    {
                        result = lineOffset;
                    }
                }

                cursor += advance;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    private int FindAnchorIndex(ulong target)
    {
        int lo = 0;
        int hi = _entries.Count - 1;
        int ans = 0;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_entries[mid].Offset <= target)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ans;
    }

    private void ThinEntries()
    {
        int oldCount = _entries.Count;
        int write = 0;
        for (int i = 0; i < oldCount; i++)
        {
            if ((i & 1) == 0 || i + 1 == oldCount)
            {
                _entries[write++] = _entries[i];
            }
        }
        _entries.RemoveRange(write, oldCount - write);
        SampleStride *= 2;
    }
}

public sealed class LogReader : IDisposable
{
    public string Path { get; }
    public SafeFileHandle Handle { get; }
    public TextEncoding Encoding { get; }
    public ulong Size { get; private set; }
    public LineIndex Index { get; } = new();
    private readonly LogChunkCache? _cache;

    private LogReader(string path, SafeFileHandle handle, TextEncoding encoding, ulong size, LogChunkCache? cache)
    {
        Path = path;
        Handle = handle;
        Encoding = encoding;
        Size = size;
        _cache = cache;
    }

    public static LogReader Open(
        string path,
        TextEncoding? encodingOverride,
        LogChunkCache? cache = null,
        FileOptions fileOptions = FileOptions.RandomAccess)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidDataException("Выберите обычный файл.");
        }

        var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            fileOptions);

        try
        {
            ulong size = (ulong)RandomAccess.GetLength(handle);
            TextEncoding encoding;
            if (encodingOverride is TextEncoding ov && ov != TextEncoding.None)
            {
                encoding = ov;
            }
            else
            {
                Span<byte> prefix = stackalloc byte[3];
                int n = cache is not null && size > 0
                    ? cache.Read(handle, size, 0, prefix)
                    : RandomAccess.Read(handle, prefix, 0);
                encoding = TextFormat.DetectStreamEncoding(prefix[..n]);
            }

            return new LogReader(path, handle, encoding, size, cache);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public ulong RefreshSize()
    {
        Size = (ulong)RandomAccess.GetLength(Handle);
        return Size;
    }

    public ulong SeekStart() => 0;

    public ulong SeekPercent(double percent) =>
        (ulong)(Size * Math.Clamp(percent / 100.0, 0.0, 1.0));

    public ulong AdvanceIndex(ulong maxBytes, CancellationToken ct)
    {
        RefreshSize();
        ulong through = Math.Min(Size, Index.ScannedTo + maxBytes);
        return Index.Advance(Handle, Encoding, through, ct);
    }

    public LogPage RenderPage(ulong offset, PageOptions options) =>
        RenderPageUntil(offset, Size, options);

    public LogPage PageAtLine(ulong offset, PageOptions options)
    {
        ulong start = Index.LineStartAt(Handle, Encoding, offset, Size);
        return RenderPage(start, options);
    }

    public LogPage RenderPageUntil(ulong offset, ulong limit, PageOptions options) =>
        ReadPageUntil(offset, limit, options, keepTail: false);

    private LogPage ReadPageUntil(ulong offset, ulong limit, PageOptions options, bool keepTail)
    {
        ulong effectiveLimit = Math.Min(limit, Size);
        ulong cursor = TextFormat.AlignOffset(Math.Min(offset, effectiveLimit), Encoding);
        int minFrag = TextFormat.MinimumFragmentBytes(Encoding);
        int rowBytes = Math.Clamp(options.MaxRowBytes, minFrag, LogIo.MaxRowBytes);
        int rowLimit = Math.Min(Math.Max(1, options.MaxRows), Math.Min(LogIo.MaxPageRows, Math.Max(1, LogIo.MaxPageBytes / rowBytes)));

        var rows = new List<LogRow>(Math.Min(rowLimit, 256));
        while (rowLimit > 0 && cursor < effectiveLimit && (keepTail || rows.Count < rowLimit))
        {
            var frag = ReadFragment(Handle, _cache, Size, cursor, effectiveLimit, rowBytes, Encoding);
            if (frag.Consumed == 0)
            {
                break;
            }

            ulong end = cursor + (ulong)frag.Consumed;
            bool continues = !frag.Terminated && end < Size;
            if (keepTail && rows.Count == rowLimit)
            {
                rows.RemoveAt(0);
            }
            rows.Add(new LogRow(cursor, end, frag.Text, continues, frag.Terminated));
            cursor = end;
        }

        return new LogPage(rows, cursor, Size, cursor >= Size);
    }

    public LogPage PageBefore(ulong offset, PageOptions options, ulong maxScanBytes = LogIo.MaxBackwardScan)
    {
        ulong target = TextFormat.AlignOffset(Math.Min(offset, Size), Encoding);
        if (target == 0)
        {
            return ReadPageUntil(0, 0, options, keepTail: false);
        }

        int unit = TextFormat.UnitSize(Encoding);
        int minFrag = TextFormat.MinimumFragmentBytes(Encoding);
        int rowBytes = Math.Clamp(options.MaxRowBytes, minFrag, LogIo.MaxRowBytes);
        int rowCount = Math.Clamp(options.MaxRows, 1, LogIo.MaxPageRows);

        bool endsAtBoundary = OffsetEndsLine(Handle, _cache, Size, target, Encoding);
        int requiredBreaks = rowCount + (endsAtBoundary ? 1 : 0);

        ulong viewportBytes = Math.Max((ulong)unit, Math.Min((ulong)rowCount * (ulong)rowBytes, (ulong)LogIo.MaxPageBytes));
        ulong byteFallback = TextFormat.AlignOffset(target > viewportBytes ? target - viewportBytes : 0, Encoding);
        ulong scanStart = TextFormat.AlignOffset(target > maxScanBytes ? target - maxScanBytes : 0, Encoding);

        ulong cursor = target;
        int found = 0;
        ulong start = byteFallback;
        bool picked = false;
        var buf = ArrayPool<byte>.Shared.Rent(LogIo.BlockSize);

        try
        {
            while (cursor > scanStart)
            {
                ulong chunkStart = cursor > (ulong)LogIo.BlockSize
                    ? Math.Max(scanStart, cursor - (ulong)LogIo.BlockSize)
                    : scanStart;
                int chunkLen = (int)(cursor - chunkStart);
                int read = ReadCached(Handle, _cache, Size, chunkStart, buf.AsSpan(0, chunkLen));
                if (read <= 0)
                {
                    break;
                }

                for (int pos = read - unit; pos >= 0; pos -= unit)
                {
                    if (LogIo.IsNewlineUnit(buf.AsSpan(pos, unit), Encoding))
                    {
                        found++;
                        if (found >= requiredBreaks)
                        {
                            ulong candidate = chunkStart + (ulong)(pos + unit);
                            start = (target - candidate <= viewportBytes) ? candidate : byteFallback;
                            picked = true;
                            break;
                        }
                    }
                }

                if (picked)
                {
                    break;
                }
                cursor = chunkStart;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        if (!picked)
        {
            start = (found < requiredBreaks && scanStart == 0 && target <= viewportBytes) ? 0 : byteFallback;
        }

        return ReadPageUntil(start, target, options, keepTail: true);
    }

    private static int ReadCached(
        SafeFileHandle handle,
        LogChunkCache? cache,
        ulong fileSize,
        ulong offset,
        Span<byte> destination) =>
        cache is not null
            ? cache.Read(handle, fileSize, offset, destination)
            : LogIo.ReadAt(handle, offset, destination);

    private static bool OffsetEndsLine(SafeFileHandle handle, LogChunkCache? cache, ulong fileSize, ulong offset, TextEncoding encoding)
    {
        int unit = TextFormat.UnitSize(encoding);
        if (offset < (ulong)unit)
        {
            return true;
        }

        Span<byte> buf = stackalloc byte[2];
        int read = ReadCached(handle, cache, fileSize, offset - (ulong)unit, buf[..unit]);
        if (read < unit)
        {
            return false;
        }

        return LogIo.IsNewlineUnit(buf[..unit], encoding) || LogIo.IsCarriageReturnUnit(buf[..unit], encoding);
    }

    private readonly record struct FragmentTextResult(string Text, int Consumed, bool Terminated);

    private static FragmentTextResult ReadFragment(
        SafeFileHandle handle,
        LogChunkCache? cache,
        ulong fileSize,
        ulong start,
        ulong limit,
        int maxBytes,
        TextEncoding encoding)
    {
        int unit = TextFormat.UnitSize(encoding);
        int cap = Math.Clamp(maxBytes, TextFormat.MinimumFragmentBytes(encoding), LogIo.MaxRowBytes);
        if (unit == 2 && (cap % 2) != 0)
        {
            cap--;
        }

        ulong remainingInFile = limit - start;
        int want = (int)Math.Min(remainingInFile, (ulong)(cap + unit * 2));
        if (want <= 0)
        {
            return new("", 0, false);
        }

        var raw = ArrayPool<byte>.Shared.Rent(want);
        try
        {
            int read = ReadCached(handle, cache, fileSize, start, raw.AsSpan(0, want));
            if (read <= 0)
            {
                return new("", 0, false);
            }

            int usableMax = Math.Min(read, cap);
            if (LogIo.TryFindNextDelimiter(raw.AsSpan(0, read), 0, encoding, out int dStart, out int dLen) &&
                dStart <= usableMax)
            {
                string line = TextFormat.DecodeLossy(raw.AsSpan(0, dStart), encoding, start == 0);
                return new(line, dStart + dLen, true);
            }

            int len = usableMax;
            if (unit == 2 && (len % 2) != 0)
            {
                len--;
            }
            else if (encoding is TextEncoding.Utf8 or TextEncoding.Utf8Bom)
            {
                len = LogIo.TrimUtf8Boundary(raw.AsSpan(0, len));
            }

            if (len == 0 && read > 0)
            {
                len = Math.Min(read, usableMax);
            }

            string fragment = TextFormat.DecodeLossy(raw.AsSpan(0, len), encoding, start == 0);
            return new(fragment, len, false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    public void Dispose() => Handle.Dispose();
}

public sealed record LogSearchQuery(string Pattern, bool Regex, bool CaseSensitive, bool WholeWord);

public sealed record LogSearchLimits(ulong MaxScannedBytes, int MaxLineBytes, int PreviewBytes)
{
    public static LogSearchLimits Default => new(
        LogIo.DefaultSearchBytes,
        LogIo.DefaultSearchLineBytes,
        LogIo.DefaultRowBytes);
}

public sealed record LogSearchHit(ulong LineStart, ulong LineEnd, string Preview);

public sealed record LogSearchPage(
    List<LogSearchHit> Hits,
    ulong NextOffset,
    ulong ScannedBytes,
    bool Complete,
    bool WorkLimitReached);

public static class LogSearch
{
    public static Regex Compile(LogSearchQuery query)
    {
        if (string.IsNullOrEmpty(query.Pattern))
        {
            throw new ArgumentException("пустой запрос");
        }

        var pattern = query.Regex ? query.Pattern : Regex.Escape(query.Pattern);
        if (query.WholeWord)
        {
            pattern = $@"\b(?:{pattern})\b";
        }

        var options = RegexOptions.CultureInvariant;
        if (!query.CaseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        try
        {
            return new Regex(pattern, options, TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Ошибка регулярного выражения: {ex.Message}", ex);
        }
    }

    public static LogSearchPage SearchPage(
        string path,
        TextEncoding? encodingOverride,
        LogSearchQuery query,
        ulong startOffset,
        int maxResults,
        LogSearchLimits limits,
        CancellationToken ct)
    {
        var regex = Compile(query);
        using var reader = LogReader.Open(path, encodingOverride, fileOptions: FileOptions.SequentialScan);
        ulong size = reader.Size;
        var encoding = reader.Encoding;
        int unit = TextFormat.UnitSize(encoding);
        int minFrag = TextFormat.MinimumFragmentBytes(encoding);
        int maxLine = Math.Clamp(limits.MaxLineBytes, minFrag, LogIo.MaxSearchLineBytes);
        int resultLimit = Math.Clamp(maxResults, 1, LogIo.MaxSearchResults);
        int previewLimit = Math.Clamp(limits.PreviewBytes, minFrag, LogIo.MaxPreviewBytes);

        ulong cursor = TextFormat.AlignOffset(Math.Min(startOffset, size), encoding);
        ulong pageStart = cursor;

        if (cursor >= size)
        {
            return new LogSearchPage([], cursor, 0, true, false);
        }

        int streamCap = Math.Clamp(LogIo.BlockSize * 2, minFrag, 256 * 1024);
        int bufCapacity = Math.Max(streamCap + 8, Math.Min(maxLine + unit * 4, 256 * 1024 + 8));
        byte[] buf = ArrayPool<byte>.Shared.Rent(bufCapacity);
        char[] charBuf = ArrayPool<char>.Shared.Rent(4096);
        var hits = new List<LogSearchHit>();
        bool workLimitReached = false;

        TryBuildPrefilter(query, encoding, out byte[]? prefilterNeedle, out bool prefilterIgnoreCaseAscii);

        try
        {
            int bufferedLen = 0;
            int pos = 0;
            ulong bufFileOffset = cursor;
            bool needSkipPartial = cursor > 0 && !IsLineBoundary(reader.Handle, cursor, encoding);
            ulong lineFileStart = cursor;
            int lineCheckCounter = 0;

            while (cursor < size && hits.Count < resultLimit)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new LogCancelledException();
                }

                // Shift unconsumed tail to front when needed.
                if (pos > 0 && pos >= bufferedLen)
                {
                    bufFileOffset += (ulong)pos;
                    bufferedLen = 0;
                    pos = 0;
                }

                int available = bufferedLen - pos;
                bool allowPrefilter = prefilterNeedle is not null && !needSkipPartial;
                bool tryPrefilterSkip = allowPrefilter
                    && available >= 1024
                    && maxLine >= buf.Length
                    && (cursor - lineFileStart) == 0;

                if (tryPrefilterSkip && cursor + (ulong)available - pageStart < limits.MaxScannedBytes)
                {
                    var window = buf.AsSpan(pos, available);
                    int skipEnd = FindSafeChunkSkipEnd(window, available, bufFileOffset + (ulong)bufferedLen < size);
                    if (skipEnd > 0 && !ContainsNeedle(window[..skipEnd], prefilterNeedle!, prefilterIgnoreCaseAscii))
                    {
                        pos += skipEnd;
                        cursor += (ulong)skipEnd;
                        lineFileStart = cursor;
                        continue;
                    }
                }

                // Try finding next delimiter in buffered data starting at current line offset.
                int lineStartRel = (int)(lineFileStart - bufFileOffset);
                if (available > 0 && TryFindDelimiterEnsuringCrlf(
                        reader.Handle,
                        buf,
                        ref bufferedLen,
                        bufFileOffset,
                        size,
                        lineStartRel,
                        pos,
                        encoding,
                        unit,
                        out int dStart,
                        out int dLen))
                {
                    int contentLen = dStart - lineStartRel;
                    if (contentLen > capAdjusted(maxLine, unit))
                    {
                        throw new LogLineLimitException(lineFileStart, limits.MaxLineBytes);
                    }

                    ulong lineEnd = bufFileOffset + (ulong)(dStart + dLen);
                    pos = dStart + dLen;

                    if (needSkipPartial)
                    {
                        needSkipPartial = false;
                        cursor = lineEnd;
                        pageStart = cursor;
                        lineFileStart = cursor;
                        continue;
                    }

                    EvaluateCandidateLine(
                        buf.AsSpan(lineStartRel, contentLen),
                        lineFileStart,
                        lineEnd,
                        encoding,
                        regex,
                        previewLimit,
                        hits,
                        ref charBuf);

                    cursor = lineEnd;
                    lineFileStart = cursor;
                    if ((++lineCheckCounter & 255) == 0 && ct.IsCancellationRequested)
                    {
                        throw new LogCancelledException();
                    }
                    if (cursor - pageStart >= limits.MaxScannedBytes && cursor < size && hits.Count < resultLimit)
                    {
                        workLimitReached = true;
                        break;
                    }
                    continue;
                }

                // No full line found yet in buffer: check line limit before refilling.
                int currentLineBytes = (bufFileOffset + (ulong)bufferedLen) > lineFileStart
                    ? (int)((bufFileOffset + (ulong)bufferedLen) - lineFileStart)
                    : 0;
                int effectiveCap = capAdjusted(maxLine, unit);

                if (currentLineBytes >= effectiveCap)
                {
                    int trimmedLen = TrimToBoundary(buf.AsSpan(lineStartRel, effectiveCap), encoding, unit);
                    ulong endAtLimit = lineFileStart + (ulong)trimmedLen;
                    if (endAtLimit < size)
                    {
                        throw new LogLineLimitException(lineFileStart, limits.MaxLineBytes);
                    }
                }

                // Shift unconsumed bytes of current line to index 0.
                if (lineStartRel > 0)
                {
                    int keep = bufferedLen - lineStartRel;
                    if (keep > 0)
                    {
                        Buffer.BlockCopy(buf, lineStartRel, buf, 0, keep);
                    }
                    bufFileOffset += (ulong)lineStartRel;
                    pos -= lineStartRel;
                    bufferedLen = keep;
                }

                // Grow buffer if a single long line needs more space (up to effectiveCap + unit * 2).
                int maxNeeded = Math.Min(effectiveCap + unit * 2, (int)Math.Min((ulong)effectiveCap + (ulong)(unit * 2), size - bufFileOffset));
                if (bufferedLen + streamCap > buf.Length && buf.Length < maxNeeded + streamCap)
                {
                    int nextSize = Math.Min(Math.Max(buf.Length * 2, bufferedLen + streamCap), maxNeeded + streamCap);
                    if (nextSize > buf.Length)
                    {
                        byte[] bigger = ArrayPool<byte>.Shared.Rent(nextSize);
                        Buffer.BlockCopy(buf, 0, bigger, 0, bufferedLen);
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = bigger;
                    }
                }

                ulong readOff = bufFileOffset + (ulong)bufferedLen;
                if (readOff >= size)
                {
                    // Flush trailing unterminated line at EOF.
                    int eofLen = bufferedLen;
                    if (eofLen > effectiveCap)
                    {
                        eofLen = TrimToBoundary(buf.AsSpan(0, effectiveCap), encoding, unit);
                        ulong endAtLimit = bufFileOffset + (ulong)eofLen;
                        if (endAtLimit < size)
                        {
                            throw new LogLineLimitException(lineFileStart, limits.MaxLineBytes);
                        }
                    }
                    else if (unit == 2 && (eofLen % 2) != 0)
                    {
                        eofLen--;
                    }

                    if (eofLen > 0)
                    {
                        ulong lineEnd = bufFileOffset + (ulong)eofLen;
                        if (needSkipPartial)
                        {
                            needSkipPartial = false;
                            cursor = lineEnd;
                            pageStart = cursor;
                        }
                        else
                        {
                            EvaluateCandidateLine(
                                buf.AsSpan(0, eofLen),
                                lineFileStart,
                                lineEnd,
                                encoding,
                                regex,
                                previewLimit,
                                hits,
                                ref charBuf);
                            cursor = lineEnd;
                        }
                    }
                    break;
                }

                int spaceLeft = buf.Length - bufferedLen;
                int wantRead = (int)Math.Min((ulong)Math.Min(streamCap, spaceLeft), size - readOff);
                int got = wantRead > 0 ? LogIo.ReadAt(reader.Handle, readOff, buf.AsSpan(bufferedLen, wantRead)) : 0;
                if (got <= 0)
                {
                    if (bufferedLen > 0 && !needSkipPartial)
                    {
                        int eofLen = unit == 2 && (bufferedLen % 2) != 0 ? bufferedLen - 1 : bufferedLen;
                        ulong lineEnd = bufFileOffset + (ulong)eofLen;
                        EvaluateCandidateLine(buf.AsSpan(0, eofLen), lineFileStart, lineEnd, encoding, regex, previewLimit, hits, ref charBuf);
                        cursor = lineEnd;
                    }
                    break;
                }

                pos = bufferedLen;
                bufferedLen += got;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            ArrayPool<char>.Shared.Return(charBuf);
        }

        return new LogSearchPage(
            hits,
            cursor,
            cursor - pageStart,
            cursor >= size,
            workLimitReached);
    }

    private static int capAdjusted(int maxBytes, int unit) =>
        unit == 2 && (maxBytes % 2) != 0 ? maxBytes - 1 : maxBytes;

    private static int TrimToBoundary(ReadOnlySpan<byte> bytes, TextEncoding encoding, int unit)
    {
        if (encoding is TextEncoding.Utf8 or TextEncoding.Utf8Bom)
        {
            return LogIo.TrimUtf8Boundary(bytes);
        }
        return unit == 2 && (bytes.Length % 2) != 0 ? bytes.Length - 1 : bytes.Length;
    }

    private static bool TryFindDelimiterEnsuringCrlf(
        SafeFileHandle handle,
        byte[] buf,
        ref int bufferedLen,
        ulong bufFileOffset,
        ulong fileSize,
        int lineStartRel,
        int searchFrom,
        TextEncoding encoding,
        int unit,
        out int dStart,
        out int dLen)
    {
        int scanStart = Math.Max(lineStartRel, searchFrom);
        if (unit == 2 && ((scanStart - lineStartRel) & 1) != 0)
        {
            scanStart--;
        }

        if (!LogIo.TryFindNextDelimiter(buf.AsSpan(0, bufferedLen), scanStart, encoding, out dStart, out dLen))
        {
            return false;
        }

        // If delimiter is CR right at the buffer end before EOF, pull next unit to check CRLF pair.
        int endAfterFirst = dStart + unit;
        if (endAfterFirst == bufferedLen && bufFileOffset + (ulong)bufferedLen < fileSize && bufferedLen + unit <= buf.Length)
        {
            if (LogIo.IsCarriageReturnUnit(buf.AsSpan(dStart, unit), encoding))
            {
                int extra = LogIo.ReadAt(handle, bufFileOffset + (ulong)bufferedLen, buf.AsSpan(bufferedLen, unit));
                if (extra > 0)
                {
                    bufferedLen += extra;
                    if (bufferedLen >= dStart + unit * 2 && LogIo.IsNewlineUnit(buf.AsSpan(dStart + unit, unit), encoding))
                    {
                        dLen = unit * 2;
                    }
                }
            }
        }

        return true;
    }

    private static void EvaluateCandidateLine(
        ReadOnlySpan<byte> content,
        ulong lineStart,
        ulong lineEnd,
        TextEncoding encoding,
        Regex regex,
        int previewLimit,
        List<LogSearchHit> hits,
        ref char[] charBuf)
    {
        int maxChars = TextFormat.GetMaxLossyCharCount(content.Length, encoding);
        if (maxChars > charBuf.Length)
        {
            ArrayPool<char>.Shared.Return(charBuf);
            charBuf = ArrayPool<char>.Shared.Rent(Math.Max(maxChars, charBuf.Length * 2));
        }

        int charLen = TextFormat.DecodeLossyChars(content, encoding, lineStart == 0, charBuf);
        ReadOnlySpan<char> chars = charBuf.AsSpan(0, charLen);
        if (regex.IsMatch(chars))
        {
            var preview = TruncatePreview(chars, previewLimit);
            hits.Add(new LogSearchHit(lineStart, lineEnd, preview));
        }
    }

    private static int FindSafeChunkSkipEnd(ReadOnlySpan<byte> window, int totalAvailable, bool moreBytesInFile)
    {
        int last = window.LastIndexOfAny((byte)'\n', (byte)'\r');
        if (last < 0) return 0;

        if (window[last] == (byte)'\r' && last + 1 == totalAvailable && moreBytesInFile)
        {
            // Back off to an earlier line ending so CRLF straddling the buffer end isn't split.
            int prev = window[..last].LastIndexOfAny((byte)'\n', (byte)'\r');
            return prev >= 0 ? prev + 1 : 0;
        }

        return last + 1;
    }

    private static bool ContainsNeedle(ReadOnlySpan<byte> haystack, byte[] needle, bool asciiIgnoreCase)
    {
        if (!asciiIgnoreCase)
        {
            return haystack.IndexOf(needle) >= 0;
        }

        if (needle.Length == 0) return true;
        byte firstLower = needle[0];
        byte firstUpper = (byte)(firstLower >= (byte)'a' && firstLower <= (byte)'z' ? firstLower - 32 : firstLower);
        int offset = 0;
        while (offset <= haystack.Length - needle.Length)
        {
            int rel = firstLower == firstUpper
                ? haystack[offset..^ (needle.Length - 1)].IndexOf(firstLower)
                : haystack[offset..^ (needle.Length - 1)].IndexOfAny(firstLower, firstUpper);
            if (rel < 0) return false;
            int idx = offset + rel;
            bool match = true;
            for (int j = 1; j < needle.Length; j++)
            {
                byte hb = haystack[idx + j];
                if (hb >= (byte)'A' && hb <= (byte)'Z') hb = (byte)(hb + 32);
                if (hb != needle[j]) { match = false; break; }
            }
            if (match) return true;
            offset = idx + 1;
        }
        return false;
    }

    private static void TryBuildPrefilter(
        LogSearchQuery query,
        TextEncoding encoding,
        out byte[]? needle,
        out bool asciiIgnoreCase)
    {
        needle = null;
        asciiIgnoreCase = false;
        if (encoding is not (TextEncoding.Utf8 or TextEncoding.Utf8Bom) || string.IsNullOrEmpty(query.Pattern))
        {
            return;
        }

        string? literal = query.Regex ? ExtractSafeRegexLiteral(query.Pattern) : query.Pattern;
        if (string.IsNullOrEmpty(literal))
        {
            return;
        }

        if (query.CaseSensitive)
        {
            needle = Encoding.UTF8.GetBytes(literal);
            return;
        }

        if (Ascii.IsValid(literal))
        {
            needle = Encoding.ASCII.GetBytes(literal.ToLowerInvariant());
            asciiIgnoreCase = true;
        }
    }

    private static string? ExtractSafeRegexLiteral(string pattern)
    {
        // Only extract a literal run when there are no top-level alternations or optional groups.
        int parenDepth = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\') { i++; continue; }
            if (c == '[')
            {
                i++;
                while (i < pattern.Length && pattern[i] != ']')
                {
                    if (pattern[i] == '\\') i++;
                    i++;
                }
                continue;
            }
            if (c == '(') parenDepth++;
            else if (c == ')') parenDepth = Math.Max(0, parenDepth - 1);
            else if (c == '|' && parenDepth == 0) return null;
        }

        string best = "";
        var cur = new StringBuilder();
        parenDepth = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c is '^' or '$') continue;
            if (c == '[')
            {
                CommitRun(ref best, cur);
                i++;
                while (i < pattern.Length && pattern[i] != ']')
                {
                    if (pattern[i] == '\\') i++;
                    i++;
                }
                continue;
            }
            if (c == '(') { CommitRun(ref best, cur); parenDepth++; continue; }
            if (c == ')') { parenDepth = Math.Max(0, parenDepth - 1); continue; }
            if (parenDepth > 0) continue;

            if (c == '\\')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] is '\\' or '.' or '-' or '_' or '=' or ':' or '/')
                {
                    char lit = pattern[i + 1];
                    i++;
                    if (IsOptionalNext(pattern, i)) { CommitRun(ref best, cur); continue; }
                    cur.Append(lit);
                    continue;
                }
                CommitRun(ref best, cur);
                i++;
                continue;
            }

            if (c is '.' or '*' or '+' or '?' or '{' or '}')
            {
                CommitRun(ref best, cur);
                if (c == '{')
                {
                    while (i < pattern.Length && pattern[i] != '}') i++;
                }
                continue;
            }

            if (IsOptionalNext(pattern, i))
            {
                CommitRun(ref best, cur);
                continue;
            }

            cur.Append(c);
        }
        CommitRun(ref best, cur);
        return best.Length >= 2 ? best : null;

        static void CommitRun(ref string best, StringBuilder sb)
        {
            if (sb.Length > best.Length) best = sb.ToString();
            sb.Clear();
        }

        static bool IsOptionalNext(string pat, int idx)
        {
            if (idx + 1 >= pat.Length) return false;
            char n = pat[idx + 1];
            return n is '?' or '*' || (n == '{' && idx + 2 < pat.Length && pat[idx + 2] == '0');
        }
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

    private static string TruncatePreview(ReadOnlySpan<char> text, int maxUtf8Bytes)
    {
        int byteCount = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            int charBytes;
            int charUnits = 1;
            if (ch < 0x80)
            {
                charBytes = 1;
            }
            else if (ch < 0x800)
            {
                charBytes = 2;
            }
            else if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                charBytes = 4;
                charUnits = 2;
            }
            else
            {
                charBytes = 3;
            }

            if (byteCount + charBytes > maxUtf8Bytes)
            {
                return new string(text[..i]);
            }
            byteCount += charBytes;
            if (charUnits == 2)
            {
                i++;
            }
        }
        return new string(text);
    }
}

public static class LogIo
{
    public const int BlockSize = 64 * 1024;
    public const int DefaultRowBytes = 16 * 1024;
    public const int MaxPageRows = 4_096;
    public const int MaxRowBytes = 1024 * 1024;
    public const int MaxPageBytes = 64 * 1024 * 1024;
    public const ulong MaxBackwardScan = 2UL * 1024 * 1024;

    public const int DefaultSearchLineBytes = 8 * 1024 * 1024;
    public const ulong DefaultSearchBytes = 512UL * 1024 * 1024;
    public const int MaxSearchLineBytes = 16 * 1024 * 1024;
    public const int MaxSearchResults = 10_000;
    public const int MaxPreviewBytes = 64 * 1024;

    public static int ReadAt(SafeFileHandle handle, ulong offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(handle, destination[total..], (long)offset + total);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    public static bool TryFindNextDelimiter(
        ReadOnlySpan<byte> span,
        int startIndex,
        TextEncoding encoding,
        out int delimiterStart,
        out int delimiterLength)
    {
        if (encoding is TextEncoding.Utf16Le or TextEncoding.Utf16Be)
        {
            bool le = encoding == TextEncoding.Utf16Le;
            int i = startIndex + (startIndex & 1);
            while (i + 1 < span.Length)
            {
                ushort u = le
                    ? BinaryPrimitives.ReadUInt16LittleEndian(span[i..])
                    : BinaryPrimitives.ReadUInt16BigEndian(span[i..]);
                if (u == '\n')
                {
                    delimiterStart = i;
                    delimiterLength = 2;
                    return true;
                }
                if (u == '\r')
                {
                    delimiterStart = i;
                    if (i + 3 < span.Length)
                    {
                        ushort next = le
                            ? BinaryPrimitives.ReadUInt16LittleEndian(span[(i + 2)..])
                            : BinaryPrimitives.ReadUInt16BigEndian(span[(i + 2)..]);
                        delimiterLength = next == '\n' ? 4 : 2;
                    }
                    else
                    {
                        delimiterLength = 2;
                    }
                    return true;
                }
                i += 2;
            }

            delimiterStart = 0;
            delimiterLength = 0;
            return false;
        }

        int rel = span[startIndex..].IndexOfAny((byte)'\n', (byte)'\r');
        if (rel < 0)
        {
            delimiterStart = 0;
            delimiterLength = 0;
            return false;
        }

        delimiterStart = startIndex + rel;
        if (span[delimiterStart] == (byte)'\r' && delimiterStart + 1 < span.Length && span[delimiterStart + 1] == (byte)'\n')
        {
            delimiterLength = 2;
        }
        else
        {
            delimiterLength = 1;
        }
        return true;
    }

    public static bool IsNewlineUnit(ReadOnlySpan<byte> unit, TextEncoding encoding) => encoding switch
    {
        TextEncoding.Utf16Le => unit.Length >= 2 && unit[0] == (byte)'\n' && unit[1] == 0,
        TextEncoding.Utf16Be => unit.Length >= 2 && unit[0] == 0 && unit[1] == (byte)'\n',
        _ => unit.Length >= 1 && unit[0] == (byte)'\n',
    };

    public static bool IsCarriageReturnUnit(ReadOnlySpan<byte> unit, TextEncoding encoding) => encoding switch
    {
        TextEncoding.Utf16Le => unit.Length >= 2 && unit[0] == (byte)'\r' && unit[1] == 0,
        TextEncoding.Utf16Be => unit.Length >= 2 && unit[0] == 0 && unit[1] == (byte)'\r',
        _ => unit.Length >= 1 && unit[0] == (byte)'\r',
    };

    public static int TrimUtf8Boundary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes[^1] < 0x80)
        {
            return bytes.Length;
        }

        int i = bytes.Length - 1;
        int cont = 0;
        while (i > 0 && (bytes[i] & 0xC0) == 0x80 && cont < 3)
        {
            i--;
            cont++;
        }

        byte lead = bytes[i];
        int expected = lead switch
        {
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 1,
        };

        int actual = bytes.Length - i;
        if (expected > 1 && actual < expected)
        {
            return i;
        }
        if ((lead & 0xC0) == 0x80)
        {
            return i;
        }
        return bytes.Length;
    }
}
