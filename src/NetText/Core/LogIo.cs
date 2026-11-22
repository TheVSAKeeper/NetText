using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
    internal bool LastAdvanceUsedParallel { get; private set; }

    public void Reset()
    {
        _entries.Clear();
        _entries.Add(new(1, 0));
        ScannedTo = 0;
        LineCount = 1;
        NextSample = IndexSampleBytes;
        SampleStride = IndexSampleBytes;
        LastAdvanceUsedParallel = false;
    }

    private static readonly nuint[] s_cachedIndexBuffers = new nuint[4];
    private const int IndexChunkStep = 256 * 1024;
    private const int IndexBufferCapacity = IndexChunkStep + 4;
    private const ulong ParallelMinSpan = 8UL * 1024 * 1024;
    private const ulong ParallelMinSliceBytes = 4UL * 1024 * 1024;
    private const int MaxParallelWorkers = 4;

    private static unsafe byte* RentIndexBuffer()
    {
        var slots = s_cachedIndexBuffers;
        for (int i = 0; i < slots.Length; i++)
        {
            if (Volatile.Read(ref slots[i]) != 0)
            {
                nuint cached = Interlocked.Exchange(ref slots[i], 0);
                if (cached != 0)
                {
                    return (byte*)cached;
                }
            }
        }
        return (byte*)NativeMemory.Alloc(IndexBufferCapacity);
    }

    private static unsafe void ReturnIndexBuffer(byte* buffer)
    {
        if (buffer is null) return;
        var slots = s_cachedIndexBuffers;
        for (int i = 0; i < slots.Length; i++)
        {
            if (Volatile.Read(ref slots[i]) == 0)
            {
                if (Interlocked.CompareExchange(ref slots[i], (nuint)buffer, 0) == 0)
                {
                    return;
                }
            }
        }
        NativeMemory.Free(buffer);
    }

    public unsafe ulong Advance(SafeFileHandle handle, TextEncoding encoding, ulong through, CancellationToken ct)
    {
        LastAdvanceUsedParallel = false;
        if (through <= ScannedTo)
        {
            return 0;
        }

        int unit = TextFormat.UnitSize(encoding);
        if (unit == 1
            && OperatingSystem.IsWindows()
            && Environment.ProcessorCount >= 2
            && (through - ScannedTo) >= ParallelMinSpan)
        {
            ulong fileLen = (ulong)RandomAccess.GetLength(handle);
            ulong effectiveThrough = Math.Min(through, fileLen);
            if (effectiveThrough <= ScannedTo)
            {
                return 0;
            }

            ulong span = effectiveThrough - ScannedTo;
            if (span >= ParallelMinSpan)
            {
                int workerCount = Math.Min(
                    Math.Clamp(Environment.ProcessorCount / 2, 2, MaxParallelWorkers),
                    (int)Math.Min((ulong)MaxParallelWorkers, span / ParallelMinSliceBytes));
                if (workerCount >= 2 && TryAdvanceParallelSingleByte(handle, effectiveThrough, workerCount, ct, out ulong advanced))
                {
                    return advanced;
                }
            }
        }

        if (SampleStride > 0)
        {
            ulong span = through - ScannedTo;
            ulong estimatedNew = span / SampleStride + 1;
            int targetCap = (int)Math.Min((ulong)MaxIndexEntries, (ulong)_entries.Count + Math.Min(estimatedNew, 4096UL));
            if (targetCap > _entries.Capacity)
            {
                _entries.EnsureCapacity(targetCap);
            }
        }

        const int chunkStep = IndexChunkStep;
        byte* buffer = RentIndexBuffer();
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
                int got = wantRead > 0 ? LogIo.ReadAt(handle, diskPos, new Span<byte>(buffer + carryLen, wantRead)) : 0;
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
                    if (singleByte)
                    {
                        if (buffer[totalAvail - 1] == (byte)'\r')
                        {
                            advanceBytes = totalAvail - 1;
                            if (advanceBytes <= 0)
                            {
                                break;
                            }
                            nextCarry = 1;
                        }
                        else
                        {
                            advanceBytes = totalAvail;
                            nextCarry = 0;
                        }
                    }
                    else
                    {
                        advanceBytes = totalAvail - unit;
                        if (advanceBytes <= 0)
                        {
                            break;
                        }
                        if ((advanceBytes % 2) != 0)
                        {
                            advanceBytes--;
                            if (advanceBytes <= 0)
                            {
                                break;
                            }
                        }
                        nextCarry = totalAvail - advanceBytes;
                    }
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
                    if (!reachedEof && (!singleByte || buffer[totalAvail - 1] == (byte)'\r'))
                    {
                        extraLookahead = LogIo.ReadAt(handle, diskPos, new Span<byte>(buffer + totalAvail, unit));
                    }
                }

                int validLen = totalAvail + extraLookahead;
                ReadOnlySpan<byte> validSpan = new(buffer, validLen);
                if (singleByte)
                {
                    ulong lineCount = LineCount;
                    ulong nextSample = NextSample;
                    ulong sampleStride = SampleStride;
                    ScanSingleByteBlock(validSpan, 0, advanceBytes, cursor, ref lineCount, ref nextSample, ref sampleStride, _entries);
                    LineCount = lineCount;
                    NextSample = nextSample;
                    SampleStride = sampleStride;
                }
                else
                {
                    int pos = 0;
                    while (pos < validLen)
                    {
                        if (!LogIo.TryFindNextDelimiter(validSpan, pos, encoding, out int dStart, out int dLen))
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
                    if (singleByte)
                    {
                        buffer[0] = (byte)'\r';
                    }
                    else
                    {
                        new ReadOnlySpan<byte>(buffer + advanceBytes, nextCarry).CopyTo(new Span<byte>(buffer, nextCarry));
                    }
                }
                carryLen = nextCarry;
            }
        }
        finally
        {
            ReturnIndexBuffer(buffer);
        }

        return ScannedTo - startScanned;
    }

    private unsafe bool TryAdvanceParallelSingleByte(
        SafeFileHandle handle,
        ulong effectiveThrough,
        int workerCount,
        CancellationToken ct,
        out ulong advanced)
    {
        ulong startScanned = ScannedTo;
        ulong span = effectiveThrough - startScanned;
        ulong alignedStep = (span / (ulong)workerCount) & ~65535UL;
        ulong initialNextSample = NextSample;
        ulong initialStride = SampleStride;
        var results = new (ulong LineDelta, List<SparseLine> Entries)[workerCount];
        int anyFallback = 0;

        try
        {
            Parallel.For(0, workerCount, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = workerCount }, w =>
            {
                SafeFileHandle? ownedHandle = null;
                if (w > 0)
                {
                    ownedHandle = LogIo.TryReOpenReadHandle(handle);
                    if (ownedHandle is null)
                    {
                        Volatile.Write(ref anyFallback, 1);
                        return;
                    }
                }

                byte* buffer = null;
                try
                {
                    ulong sStart = startScanned + (ulong)w * alignedStep;
                    ulong sEnd = w == workerCount - 1 ? effectiveThrough : sStart + alignedStep;
                    SafeFileHandle wHandle = w == 0 ? handle : ownedHandle!;
                    buffer = RentIndexBuffer();
                    ulong cursor = sStart;
                    ulong diskPos = sStart;
                    int carryLen = 0;

                    ulong localLines = 0;
                    ulong localStride = initialStride;
                    ulong localNextSample = w == 0 ? initialNextSample : sStart;
                    ulong sliceSpan = sEnd - sStart;
                    int estEntries = initialStride > 0 ? (int)Math.Min((ulong)MaxIndexEntries, sliceSpan / initialStride + 2) : 16;
                    var localEntries = new List<SparseLine>(Math.Min(estEntries, 4096));

                    while (cursor < sEnd)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref anyFallback) != 0)
                        {
                            return;
                        }

                        ulong remaining = sEnd > diskPos ? sEnd - diskPos : 0;
                        int wantRead = (int)Math.Min((ulong)IndexChunkStep, remaining);
                        int got = wantRead > 0 ? LogIo.ReadAt(wHandle, diskPos, new Span<byte>(buffer + carryLen, wantRead)) : 0;
                        diskPos += (ulong)got;

                        if (got < wantRead)
                        {
                            Volatile.Write(ref anyFallback, 1);
                            return;
                        }

                        int totalAvail = carryLen + got;
                        if (totalAvail == 0)
                        {
                            break;
                        }

                        bool moreInBatch = diskPos < sEnd;
                        int advanceBytes;
                        int nextCarry = 0;
                        int extraLookahead = 0;

                        if (moreInBatch)
                        {
                            if (buffer[totalAvail - 1] == (byte)'\r')
                            {
                                advanceBytes = totalAvail - 1;
                                if (advanceBytes <= 0)
                                {
                                    break;
                                }
                                nextCarry = 1;
                            }
                            else
                            {
                                advanceBytes = totalAvail;
                                nextCarry = 0;
                            }
                        }
                        else
                        {
                            advanceBytes = totalAvail;
                            if (buffer[totalAvail - 1] == (byte)'\r')
                            {
                                extraLookahead = LogIo.ReadAt(wHandle, diskPos, new Span<byte>(buffer + totalAvail, 1));
                                if (w < workerCount - 1 && extraLookahead == 0)
                                {
                                    Volatile.Write(ref anyFallback, 1);
                                    return;
                                }
                            }
                        }

                        int validLen = totalAvail + extraLookahead;
                        ReadOnlySpan<byte> validSpan = new(buffer, validLen);
                        ScanSingleByteBlock(validSpan, 0, advanceBytes, cursor, ref localLines, ref localNextSample, ref localStride, localEntries);

                        cursor += (ulong)advanceBytes;

                        if (nextCarry > 0)
                        {
                            buffer[0] = (byte)'\r';
                        }
                        carryLen = nextCarry;
                    }

                    results[w] = (localLines, localEntries);
                }
                finally
                {
                    ReturnIndexBuffer(buffer);
                    ownedHandle?.Dispose();
                }
            });
        }
        catch (AggregateException ae)
        {
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
        }

        if (Volatile.Read(ref anyFallback) != 0)
        {
            advanced = 0;
            return false;
        }

        int totalLocalEntries = 0;
        for (int w = 0; w < workerCount; w++)
        {
            totalLocalEntries += results[w].Entries.Count;
        }
        int targetCap = Math.Min(MaxIndexEntries, _entries.Count + totalLocalEntries);
        if (targetCap > _entries.Capacity)
        {
            _entries.EnsureCapacity(targetCap);
        }

        ulong runningLines = LineCount;
        ulong nextSample = NextSample;
        ulong stride = SampleStride;
        for (int w = 0; w < workerCount; w++)
        {
            var entries = results[w].Entries;
            for (int j = 0; j < entries.Count; j++)
            {
                var e = entries[j];
                if (e.Offset >= nextSample)
                {
                    if (_entries.Count >= MaxIndexEntries)
                    {
                        ThinEntries(_entries, ref stride);
                    }
                    _entries.Add(new SparseLine(runningLines + e.Line, e.Offset));
                    nextSample = e.Offset + stride;
                }
            }
            runningLines += results[w].LineDelta;
        }

        LineCount = runningLines;
        NextSample = nextSample;
        SampleStride = stride;
        ScannedTo = effectiveThrough;
        LastAdvanceUsedParallel = true;
        advanced = ScannedTo - startScanned;
        return true;
    }

    private static unsafe void ScanSingleByteBlock(
        ReadOnlySpan<byte> block,
        int startPos,
        int advanceBytes,
        ulong startFileOffset,
        ref ulong lineCount,
        ref ulong nextSample,
        ref ulong sampleStride,
        List<SparseLine> entries)
    {
        int endPos = startPos + advanceBytes;
        int i = startPos;
        ulong localLines = lineCount;
        ulong localNextSample = nextSample;
        ulong localStride = sampleStride;

        fixed (byte* pBase = block)
        {
            if (Avx512BW.IsSupported || Vector512.IsHardwareAccelerated)
            {
                var vLf = Vector512.Create((byte)'\n');
                var vCr = Vector512.Create((byte)'\r');
                while (i + 64 <= endPos)
                {
                    ulong fileOff = startFileOffset + (ulong)(i - startPos);
                    int fastLimit = localNextSample > fileOff
                        ? (int)Math.Min((ulong)endPos, (ulong)i + (localNextSample - fileOff - 1))
                        : i;
                    while (i + 256 <= fastLimit)
                    {
                        var v0 = Vector512.Load(pBase + i);
                        var v1 = Vector512.Load(pBase + i + 64);
                        var v2 = Vector512.Load(pBase + i + 128);
                        var v3 = Vector512.Load(pBase + i + 192);

                        ulong lf0 = Vector512.Equals(v0, vLf).ExtractMostSignificantBits();
                        ulong cr0 = Vector512.Equals(v0, vCr).ExtractMostSignificantBits();
                        ulong lf1 = Vector512.Equals(v1, vLf).ExtractMostSignificantBits();
                        ulong cr1 = Vector512.Equals(v1, vCr).ExtractMostSignificantBits();
                        ulong lf2 = Vector512.Equals(v2, vLf).ExtractMostSignificantBits();
                        ulong cr2 = Vector512.Equals(v2, vCr).ExtractMostSignificantBits();
                        ulong lf3 = Vector512.Equals(v3, vLf).ExtractMostSignificantBits();
                        ulong cr3 = Vector512.Equals(v3, vCr).ExtractMostSignificantBits();

                        ulong nextIsLf3 = ((cr3 & 0x8000_0000_0000_0000UL) != 0 && i + 256 < block.Length && pBase[i + 256] == (byte)'\n') ? 1UL : 0UL;
                        ulong end0 = lf0 | (cr0 & ~((lf0 >> 1) | (lf1 << 63)));
                        ulong end1 = lf1 | (cr1 & ~((lf1 >> 1) | (lf2 << 63)));
                        ulong end2 = lf2 | (cr2 & ~((lf2 >> 1) | (lf3 << 63)));
                        ulong end3 = lf3 | (cr3 & ~((lf3 >> 1) | (nextIsLf3 << 63)));

                        localLines += (ulong)(
                            BitOperations.PopCount(end0) +
                            BitOperations.PopCount(end1) +
                            BitOperations.PopCount(end2) +
                            BitOperations.PopCount(end3));

                        i += 256;
                    }

                    if (i + 64 > endPos)
                    {
                        break;
                    }

                    var v = Vector512.Load(pBase + i);
                    ulong lfMask = Vector512.Equals(v, vLf).ExtractMostSignificantBits();
                    ulong crMask = Vector512.Equals(v, vCr).ExtractMostSignificantBits();
                    ulong nextIsLf = ((crMask & 0x8000_0000_0000_0000UL) != 0 && i + 64 < block.Length && pBase[i + 64] == (byte)'\n') ? 1UL : 0UL;
                    ulong shiftedLf = (lfMask >> 1) | (nextIsLf << 63);
                    ulong endMask = lfMask | (crMask & ~shiftedLf);

                    ulong vecFileStart = startFileOffset + (ulong)(i - startPos);
                    if (vecFileStart + 64 < localNextSample)
                    {
                        localLines += (ulong)BitOperations.PopCount(endMask);
                    }
                    else
                    {
                        while (endMask != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(endMask);
                            localLines++;
                            RecordSampleInto(entries, localLines, vecFileStart + (ulong)bit + 1UL, ref localNextSample, ref localStride);
                            endMask &= endMask - 1;
                        }
                    }

                    i += 64;
                }
            }

            if (Avx2.IsSupported || Vector256.IsHardwareAccelerated)
            {
                var vLf = Vector256.Create((byte)'\n');
                var vCr = Vector256.Create((byte)'\r');
                while (i + 32 <= endPos)
                {
                    ulong fileOff = startFileOffset + (ulong)(i - startPos);
                    int fastLimit = localNextSample > fileOff
                        ? (int)Math.Min((ulong)endPos, (ulong)i + (localNextSample - fileOff - 1))
                        : i;
                    while (i + 128 <= fastLimit)
                    {
                        var v0 = Vector256.Load(pBase + i);
                        var v1 = Vector256.Load(pBase + i + 32);
                        var v2 = Vector256.Load(pBase + i + 64);
                        var v3 = Vector256.Load(pBase + i + 96);

                        uint lf0 = Vector256.Equals(v0, vLf).ExtractMostSignificantBits();
                        uint cr0 = Vector256.Equals(v0, vCr).ExtractMostSignificantBits();
                        uint lf1 = Vector256.Equals(v1, vLf).ExtractMostSignificantBits();
                        uint cr1 = Vector256.Equals(v1, vCr).ExtractMostSignificantBits();
                        uint lf2 = Vector256.Equals(v2, vLf).ExtractMostSignificantBits();
                        uint cr2 = Vector256.Equals(v2, vCr).ExtractMostSignificantBits();
                        uint lf3 = Vector256.Equals(v3, vLf).ExtractMostSignificantBits();
                        uint cr3 = Vector256.Equals(v3, vCr).ExtractMostSignificantBits();

                        uint nextIsLf3 = ((cr3 & 0x80000000U) != 0 && i + 128 < block.Length && pBase[i + 128] == (byte)'\n') ? 1U : 0U;
                        uint end0 = lf0 | (cr0 & ~((lf0 >> 1) | (lf1 << 31)));
                        uint end1 = lf1 | (cr1 & ~((lf1 >> 1) | (lf2 << 31)));
                        uint end2 = lf2 | (cr2 & ~((lf2 >> 1) | (lf3 << 31)));
                        uint end3 = lf3 | (cr3 & ~((lf3 >> 1) | (nextIsLf3 << 31)));

                        localLines += (ulong)(
                            BitOperations.PopCount(((ulong)end1 << 32) | end0) +
                            BitOperations.PopCount(((ulong)end3 << 32) | end2));

                        i += 128;
                    }

                    if (i + 32 > endPos)
                    {
                        break;
                    }

                    var v = Vector256.Load(pBase + i);
                    uint lfMask = Vector256.Equals(v, vLf).ExtractMostSignificantBits();
                    uint crMask = Vector256.Equals(v, vCr).ExtractMostSignificantBits();
                    uint nextIsLf = ((crMask & 0x80000000U) != 0 && i + 32 < block.Length && pBase[i + 32] == (byte)'\n') ? 1U : 0U;
                    uint shiftedLf = (lfMask >> 1) | (nextIsLf << 31);
                    uint endMask = lfMask | (crMask & ~shiftedLf);

                    ulong vecFileStart = startFileOffset + (ulong)(i - startPos);
                    if (vecFileStart + 32 < localNextSample)
                    {
                        localLines += (ulong)BitOperations.PopCount(endMask);
                    }
                    else
                    {
                        while (endMask != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(endMask);
                            localLines++;
                            RecordSampleInto(entries, localLines, vecFileStart + (ulong)bit + 1UL, ref localNextSample, ref localStride);
                            endMask &= endMask - 1;
                        }
                    }

                    i += 32;
                }
            }

            if (Vector128.IsHardwareAccelerated)
            {
                var vLf = Vector128.Create((byte)'\n');
                var vCr = Vector128.Create((byte)'\r');
                while (i + 16 <= endPos)
                {
                    ulong fileOff = startFileOffset + (ulong)(i - startPos);
                    int fastLimit = localNextSample > fileOff
                        ? (int)Math.Min((ulong)endPos, (ulong)i + (localNextSample - fileOff - 1))
                        : i;
                    if (Sse2.IsSupported)
                    {
                        var zero = Vector128<byte>.Zero;
                        int sadLimit = Math.Min(fastLimit, block.Length - 1);
                        while (i + 64 <= sadLimit)
                        {
                            int batchEnd = Math.Min(sadLimit, i + 64 * 120);
                            var acc0 = zero;
                            var acc1 = zero;
                            while (i + 64 <= batchEnd)
                            {
                                var v0 = Vector128.Load(pBase + i);
                                var v1 = Vector128.Load(pBase + i + 16);
                                var v2 = Vector128.Load(pBase + i + 32);
                                var v3 = Vector128.Load(pBase + i + 48);

                                var lf0 = Vector128.Equals(v0, vLf);
                                var cr0 = Vector128.Equals(v0, vCr);
                                var lf1 = Vector128.Equals(v1, vLf);
                                var cr1 = Vector128.Equals(v1, vCr);
                                var lf2 = Vector128.Equals(v2, vLf);
                                var cr2 = Vector128.Equals(v2, vCr);
                                var lf3 = Vector128.Equals(v3, vLf);
                                var cr3 = Vector128.Equals(v3, vCr);

                                var nextLf0 = Sse2.ShiftRightLogical128BitLane(lf0, 1) | Sse2.ShiftLeftLogical128BitLane(lf1, 15);
                                var nextLf1 = Sse2.ShiftRightLogical128BitLane(lf1, 1) | Sse2.ShiftLeftLogical128BitLane(lf2, 15);
                                var nextLf2 = Sse2.ShiftRightLogical128BitLane(lf2, 1) | Sse2.ShiftLeftLogical128BitLane(lf3, 15);
                                var nextLf3 = Vector128.Equals(Vector128.Load(pBase + i + 49), vLf);

                                acc0 -= lf0 | Vector128.AndNot(cr0, nextLf0);
                                acc1 -= lf1 | Vector128.AndNot(cr1, nextLf1);
                                acc0 -= lf2 | Vector128.AndNot(cr2, nextLf2);
                                acc1 -= lf3 | Vector128.AndNot(cr3, nextLf3);

                                i += 64;
                            }
                            var s0 = Sse2.SumAbsoluteDifferences(acc0, zero).AsUInt64();
                            var s1 = Sse2.SumAbsoluteDifferences(acc1, zero).AsUInt64();
                            localLines += s0.GetElement(0) + s0.GetElement(1) + s1.GetElement(0) + s1.GetElement(1);
                        }
                    }
                    else
                    {
                        while (i + 64 <= fastLimit)
                        {
                            var v0 = Vector128.Load(pBase + i);
                            var v1 = Vector128.Load(pBase + i + 16);
                            var v2 = Vector128.Load(pBase + i + 32);
                            var v3 = Vector128.Load(pBase + i + 48);

                            uint lf0 = Vector128.Equals(v0, vLf).ExtractMostSignificantBits();
                            uint cr0 = Vector128.Equals(v0, vCr).ExtractMostSignificantBits();
                            uint lf1 = Vector128.Equals(v1, vLf).ExtractMostSignificantBits();
                            uint cr1 = Vector128.Equals(v1, vCr).ExtractMostSignificantBits();
                            uint lf2 = Vector128.Equals(v2, vLf).ExtractMostSignificantBits();
                            uint cr2 = Vector128.Equals(v2, vCr).ExtractMostSignificantBits();
                            uint lf3 = Vector128.Equals(v3, vLf).ExtractMostSignificantBits();
                            uint cr3 = Vector128.Equals(v3, vCr).ExtractMostSignificantBits();

                            uint nextIsLf3 = ((cr3 & 0x8000U) != 0 && i + 64 < block.Length && pBase[i + 64] == (byte)'\n') ? 1U : 0U;
                            uint end0 = lf0 | (cr0 & ~(((lf0 >> 1) | (lf1 << 15)) & 0xFFFFU));
                            uint end1 = lf1 | (cr1 & ~(((lf1 >> 1) | (lf2 << 15)) & 0xFFFFU));
                            uint end2 = lf2 | (cr2 & ~(((lf2 >> 1) | (lf3 << 15)) & 0xFFFFU));
                            uint end3 = lf3 | (cr3 & ~(((lf3 >> 1) | (nextIsLf3 << 15)) & 0xFFFFU));

                            ulong packed = (ulong)end0 | ((ulong)end1 << 16) | ((ulong)end2 << 32) | ((ulong)end3 << 48);
                            localLines += (ulong)BitOperations.PopCount(packed);

                            i += 64;
                        }
                    }

                    if (i + 16 > endPos)
                    {
                        break;
                    }

                    var v = Vector128.Load(pBase + i);
                    uint lfMask = Vector128.Equals(v, vLf).ExtractMostSignificantBits();
                    uint crMask = Vector128.Equals(v, vCr).ExtractMostSignificantBits();
                    uint nextIsLf = ((crMask & 0x8000U) != 0 && i + 16 < block.Length && pBase[i + 16] == (byte)'\n') ? 1U : 0U;
                    uint shiftedLf = (lfMask >> 1) | (nextIsLf << 15);
                    uint endMask = lfMask | (crMask & ~shiftedLf);

                    ulong vecFileStart = startFileOffset + (ulong)(i - startPos);
                    if (vecFileStart + 16 < localNextSample)
                    {
                        localLines += (ulong)BitOperations.PopCount(endMask);
                    }
                    else
                    {
                        while (endMask != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(endMask);
                            localLines++;
                            RecordSampleInto(entries, localLines, vecFileStart + (ulong)bit + 1UL, ref localNextSample, ref localStride);
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
                localLines++;
                ulong lineOffset = startFileOffset + (ulong)(i - startPos) + 1UL;
                if (lineOffset >= localNextSample)
                {
                    RecordSampleInto(entries, localLines, lineOffset, ref localNextSample, ref localStride);
                }
            }
            i++;
        }

        lineCount = localLines;
        nextSample = localNextSample;
        sampleStride = localStride;
    }

    private static void RecordSampleInto(
        List<SparseLine> entries,
        ulong lineCount,
        ulong lineOffset,
        ref ulong nextSample,
        ref ulong sampleStride)
    {
        if (lineOffset < nextSample) return;
        if (entries.Count >= MaxIndexEntries)
        {
            ThinEntries(entries, ref sampleStride);
        }
        entries.Add(new(lineCount, lineOffset));
        nextSample = lineOffset + sampleStride;
    }

    private void RecordLine(ulong lineOffset)
    {
        LineCount++;
        if (lineOffset >= NextSample)
        {
            ulong next = NextSample;
            ulong stride = SampleStride;
            RecordSampleInto(_entries, LineCount, lineOffset, ref next, ref stride);
            NextSample = next;
            SampleStride = stride;
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
        => LineAtFromAnchor(handle, encoding, offset, fileSize, anchor).Offset;

    public static SparseLine LineAtFromAnchor(SafeFileHandle handle, TextEncoding encoding, ulong offset, ulong fileSize, SparseLine anchor)
    {
        ulong target = TextFormat.AlignOffset(Math.Min(offset, fileSize), encoding);
        ulong anchorOffset = anchor.Offset;
        if (anchorOffset >= target)
        {
            return anchor;
        }

        int unit = TextFormat.UnitSize(encoding);
        int overlap = unit * 2;
        var buffer = ArrayPool<byte>.Shared.Rent(overlap + LogIo.BlockSize + unit);
        ulong cursor = anchorOffset;
        ulong result = anchorOffset;
        ulong line = anchor.Line;

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
                        line++;
                    }
                }

                cursor += advance;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new SparseLine(line, result);
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

    private static void ThinEntries(List<SparseLine> entries, ref ulong sampleStride)
    {
        int oldCount = entries.Count;
        int write = 0;
        for (int i = 0; i < oldCount; i++)
        {
            if ((i & 1) == 0 || i + 1 == oldCount)
            {
                entries[write++] = entries[i];
            }
        }
        entries.RemoveRange(write, oldCount - write);
        sampleStride *= 2;
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
        FileOptions fileOptions = FileOptions.None)
    {
        var handle = LogIo.OpenReadHandle(path, fileOptions);

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

    internal ulong? NearbyLineStart(ulong offset, ulong minimum = 0)
    {
        ulong target = TextFormat.AlignOffset(Math.Min(offset, Size), Encoding);
        int unit = TextFormat.UnitSize(Encoding);
        ulong lower = TextFormat.AlignOffset(Math.Max(minimum, target > LogIo.MaxBackwardScan ? target - LogIo.MaxBackwardScan : 0), Encoding);
        if (target <= lower)
        {
            return lower == minimum ? minimum : null;
        }

        int maxChunk = (int)Math.Min((ulong)LogIo.BlockSize, target - lower);
        var buffer = ArrayPool<byte>.Shared.Rent(maxChunk + unit);
        try
        {
            int step = 4096;
            for (ulong end = target; end > lower;)
            {
                ulong start = Math.Max(lower, end > (ulong)step ? end - (ulong)step : 0);
                int length = (int)(end - start);
                int read = ReadCached(Handle, _cache, Size, start, buffer.AsSpan(0, length + unit));
                if (unit == 1)
                {
                    int scanLen = Math.Min(length, read);
                    ReadOnlySpan<byte> slice = buffer.AsSpan(0, scanLen);
                    while (!slice.IsEmpty)
                    {
                        int position = slice.LastIndexOfAny((byte)'\r', (byte)'\n');
                        if (position < 0)
                        {
                            break;
                        }
                        if (buffer[position] == (byte)'\n')
                        {
                            return start + (ulong)(position + 1);
                        }
                        if (position + 2 > read || buffer[position + 1] != (byte)'\n')
                        {
                            return start + (ulong)(position + 1);
                        }
                        slice = slice[..position];
                    }
                }
                else
                {
                    for (int position = Math.Min(length, read - read % unit) - unit; position >= 0; position -= unit)
                    {
                        var codeUnit = buffer.AsSpan(position, unit);
                        if (LogIo.IsNewlineUnit(codeUnit, Encoding)) return start + (ulong)(position + unit);
                        if (LogIo.IsCarriageReturnUnit(codeUnit, Encoding) &&
                            (position + unit * 2 > read || !LogIo.IsNewlineUnit(buffer.AsSpan(position + unit, unit), Encoding)))
                            return start + (ulong)(position + unit);
                    }
                }
                step = LogIo.BlockSize;
                end = start;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return lower == minimum ? minimum : null;
    }

    public LogPage RenderPageUntil(ulong offset, ulong limit, PageOptions options) =>
        ReadPageUntil(offset, limit, options, keepTail: false);

    private LogPage ReadPageUntil(ulong offset, ulong limit, PageOptions options, bool keepTail)
    {
        ulong effectiveLimit = Math.Min(limit, Size);
        ulong cursor = TextFormat.AlignOffset(Math.Min(offset, effectiveLimit), Encoding);
        int unit = TextFormat.UnitSize(Encoding);
        int minFrag = TextFormat.MinimumFragmentBytes(Encoding);
        int rowBytes = Math.Clamp(options.MaxRowBytes, minFrag, LogIo.MaxRowBytes);
        int rowLimit = Math.Min(Math.Max(1, options.MaxRows), Math.Min(LogIo.MaxPageRows, Math.Max(1, LogIo.MaxPageBytes / rowBytes)));
        int cap = unit == 2 && (rowBytes % 2) != 0 ? rowBytes - 1 : rowBytes;

        var rows = new List<LogRow>(Math.Min(rowLimit, 256));
        if (rowLimit <= 0 || cursor >= effectiveLimit)
        {
            return new LogPage(rows, cursor, Size, cursor >= Size);
        }

        ulong remainingTotal = effectiveLimit - cursor;
        int windowBytes = (int)Math.Min(
            remainingTotal,
            (ulong)Math.Min(128 * 1024, Math.Max(rowBytes + unit * 2, Math.Min(LogIo.MaxPageBytes, rowLimit * 256))));
        byte[] buf = ArrayPool<byte>.Shared.Rent(windowBytes);

        try
        {
            ulong bufFileOffset = cursor;
            int bufferedLen = 0;
            int pos = 0;
            bool eofReached = false;

            while (cursor < effectiveLimit && (keepTail || rows.Count < rowLimit))
            {
                if (pos > 0)
                {
                    int unparsed = bufferedLen - pos;
                    if (unparsed > 0)
                    {
                        Buffer.BlockCopy(buf, pos, buf, 0, unparsed);
                    }
                    bufFileOffset += (ulong)pos;
                    bufferedLen = unparsed;
                    pos = 0;
                }

                ulong readOffset = bufFileOffset + (ulong)bufferedLen;
                ulong remainingToLimit = effectiveLimit > readOffset ? effectiveLimit - readOffset : 0;
                if (remainingToLimit > 0 && !eofReached)
                {
                    int targetWindow = (int)Math.Min(remainingToLimit + (ulong)bufferedLen, (ulong)windowBytes);
                    if (targetWindow > buf.Length)
                    {
                        byte[] bigger = ArrayPool<byte>.Shared.Rent(targetWindow);
                        if (bufferedLen > 0)
                        {
                            Buffer.BlockCopy(buf, 0, bigger, 0, bufferedLen);
                        }
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = bigger;
                    }

                    int space = Math.Min(buf.Length, targetWindow) - bufferedLen;
                    int want = (int)Math.Min((ulong)space, remainingToLimit);
                    if (want > 0)
                    {
                        int got = ReadCached(Handle, _cache, Size, readOffset, buf.AsSpan(bufferedLen, want));
                        if (got < want)
                        {
                            eofReached = true;
                        }
                        bufferedLen += got;
                    }
                }

                if (bufferedLen == 0)
                {
                    break;
                }

                bool isEndOfInput = eofReached || (bufFileOffset + (ulong)bufferedLen >= effectiveLimit);
                int posBefore = pos;
                int rowsBefore = rows.Count;
                ParsePageFromBuffer(
                    buf.AsSpan(0, bufferedLen),
                    ref pos,
                    ref cursor,
                    Size,
                    Encoding,
                    unit,
                    cap,
                    rowLimit,
                    keepTail,
                    isEndOfInput,
                    rows);

                if (pos == posBefore && rows.Count == rowsBefore)
                {
                    if (isEndOfInput)
                    {
                        break;
                    }
                    windowBytes = Math.Max(windowBytes * 2, cap + unit * 2);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
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

        ulong viewportBytes = Math.Max((ulong)unit, Math.Min((ulong)rowCount * (ulong)rowBytes, (ulong)LogIo.MaxPageBytes));
        ulong byteFallback = TextFormat.AlignOffset(target > viewportBytes ? target - viewportBytes : 0, Encoding);
        ulong scanStart = TextFormat.AlignOffset(target > maxScanBytes ? target - maxScanBytes : 0, Encoding);

        ulong cursor = target;
        int found = 0;
        int requiredBreaks = rowCount;
        ulong start = byteFallback;
        bool picked = false;
        bool firstChunk = true;
        bool bufCoversTarget = false;
        ulong bufChunkStart = 0;
        int bufRead = 0;
        int chunkStep = Math.Min(LogIo.BlockSize, Math.Max(16 * 1024, rowCount * 256));
        int maxRent = target > scanStart ? (int)Math.Min((ulong)LogIo.BlockSize, target - scanStart) : 0;
        var buf = maxRent > 0 ? ArrayPool<byte>.Shared.Rent(maxRent) : [];

        try
        {
            while (cursor > scanStart)
            {
                ulong step = (ulong)chunkStep;
                ulong chunkStart = cursor > step
                    ? Math.Max(scanStart, cursor - step)
                    : scanStart;
                int chunkLen = (int)(cursor - chunkStart);
                int read = ReadCached(Handle, _cache, Size, chunkStart, buf.AsSpan(0, chunkLen));
                if (read <= 0)
                {
                    break;
                }

                if (firstChunk)
                {
                    firstChunk = false;
                    if (chunkStart + (ulong)read == target && read >= unit)
                    {
                        bufCoversTarget = true;
                        bufChunkStart = chunkStart;
                        bufRead = read;
                        var lastUnit = buf.AsSpan(read - unit, unit);
                        if (LogIo.IsNewlineUnit(lastUnit, Encoding) || LogIo.IsCarriageReturnUnit(lastUnit, Encoding))
                        {
                            requiredBreaks = rowCount + 1;
                        }
                    }
                }
                else
                {
                    bufCoversTarget = false;
                }

                if (unit == 1)
                {
                    ReadOnlySpan<byte> slice = buf.AsSpan(0, read);
                    while (!slice.IsEmpty)
                    {
                        int pos = slice.LastIndexOf((byte)'\n');
                        if (pos < 0)
                        {
                            break;
                        }
                        found++;
                        if (found >= requiredBreaks)
                        {
                            ulong candidate = chunkStart + (ulong)(pos + 1);
                            start = (target - candidate <= viewportBytes) ? candidate : byteFallback;
                            picked = true;
                            break;
                        }
                        slice = slice[..pos];
                    }
                }
                else
                {
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
                }

                if (picked)
                {
                    break;
                }
                chunkStep = LogIo.BlockSize;
                cursor = chunkStart;
            }

            if (!picked)
            {
                start = (found < requiredBreaks && scanStart == 0 && target <= viewportBytes) ? 0 : byteFallback;
            }

            if (bufCoversTarget && start >= bufChunkStart && start <= target)
            {
                int rowLimit = Math.Min(rowCount, Math.Min(LogIo.MaxPageRows, Math.Max(1, LogIo.MaxPageBytes / rowBytes)));
                int cap = unit == 2 && (rowBytes % 2) != 0 ? rowBytes - 1 : rowBytes;
                var rows = new List<LogRow>(Math.Min(rowLimit, 256));
                int parsePos = (int)(start - bufChunkStart);
                ulong parseCursor = start;
                ParsePageFromBuffer(
                    buf.AsSpan(0, bufRead),
                    ref parsePos,
                    ref parseCursor,
                    Size,
                    Encoding,
                    unit,
                    cap,
                    rowLimit,
                    keepTail: true,
                    isEndOfInput: true,
                    rows);
                return new LogPage(rows, parseCursor, Size, parseCursor >= Size);
            }
        }
        finally
        {
            if (buf.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
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

    private static void ParsePageFromBuffer(
        ReadOnlySpan<byte> buffer,
        ref int pos,
        ref ulong cursor,
        ulong fileSize,
        TextEncoding encoding,
        int unit,
        int cap,
        int rowLimit,
        bool keepTail,
        bool isEndOfInput,
        List<LogRow> rows)
    {
        while (pos < buffer.Length && (keepTail || rows.Count < rowLimit))
        {
            ReadOnlySpan<byte> remaining = buffer[pos..];
            if (LogIo.TryFindNextDelimiter(remaining, 0, encoding, out int dStart, out int dLen) && dStart <= cap)
            {
                if (!isEndOfInput && dStart + unit * 2 > remaining.Length &&
                    LogIo.IsCarriageReturnUnit(remaining.Slice(dStart, unit), encoding))
                {
                    break;
                }

                ulong rowStart = cursor;
                int consumed = dStart + dLen;
                ulong rowEnd = rowStart + (ulong)consumed;
                string line = TextFormat.DecodeLossy(remaining[..dStart], encoding, rowStart == 0);
                if (keepTail && rows.Count == rowLimit)
                {
                    rows.RemoveAt(0);
                }
                rows.Add(new LogRow(rowStart, rowEnd, line, Continues: false, Terminated: true));
                pos += consumed;
                cursor = rowEnd;
                continue;
            }

            if (!isEndOfInput && remaining.Length < cap + unit * 2)
            {
                break;
            }

            int usableMax = Math.Min(remaining.Length, cap);
            int len = usableMax;
            if (unit == 2 && (len % 2) != 0)
            {
                len--;
            }
            else if (encoding is TextEncoding.Utf8 or TextEncoding.Utf8Bom && (!isEndOfInput || len < remaining.Length))
            {
                len = LogIo.TrimUtf8Boundary(remaining[..len]);
            }

            if (len == 0 && remaining.Length > 0)
            {
                len = usableMax;
            }

            ulong fragStart = cursor;
            ulong fragEnd = fragStart + (ulong)len;
            bool continues = fragEnd < fileSize;
            string fragment = TextFormat.DecodeLossy(remaining[..len], encoding, fragStart == 0);
            if (keepTail && rows.Count == rowLimit)
            {
                rows.RemoveAt(0);
            }
            rows.Add(new LogRow(fragStart, fragEnd, fragment, continues, Terminated: false));
            pos += len;
            cursor = fragEnd;
        }
    }

    public byte[] ReadMinimapWidths(ulong start = 0, ulong? fileLength = null, int bands = 32, CancellationToken ct = default) =>
        LogIo.ReadMinimapWidths(Handle, fileLength ?? Size, Encoding, _cache, start, bands, ct);

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

public sealed record LogSearchHit(ulong LineStart, ulong LineEnd, string Preview, ulong? Line = null);

public sealed record LogSearchPage(
    List<LogSearchHit> Hits,
    ulong NextOffset,
    ulong ScannedBytes,
    bool Complete,
    bool WorkLimitReached,
    ulong? PendingLineStart = null,
    ulong FileSize = 0);

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

    public static unsafe LogSearchPage SearchPage(
        string path,
        TextEncoding? encodingOverride,
        LogSearchQuery query,
        ulong startOffset,
        int maxResults,
        LogSearchLimits limits,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(query.Pattern))
        {
            throw new ArgumentException("пустой запрос");
        }

        bool plainCaseSensitiveLiteral = !query.Regex && !query.WholeWord && query.CaseSensitive;
        Regex? regex = plainCaseSensitiveLiteral ? null : Compile(query);

        using var handle = LogIo.OpenReadHandle(path, FileOptions.SequentialScan);
        ulong size = (ulong)RandomAccess.GetLength(handle);
        if (size == 0)
        {
            return new LogSearchPage([], 0, 0, true, false, FileSize: 0);
        }

        const int initialReadBytes = 16 * 1024;
        const int sohPoolCap = 64 * 1024;
        const int streamCap = 480 * 1024;

        byte[]? pooledBuf = null;
        char[]? charBuf = null;
        byte* nativeBuf = null;
        int initialGot = 0;

        var hits = new List<LogSearchHit>(Math.Min(Math.Clamp(maxResults, 1, LogIo.MaxSearchResults), 64));
        ulong cursor = 0;
        ulong pageStart = 0;
        bool workLimitReached = false;
        ulong? pendingLine = null;

        try
        {
            TextEncoding encoding;
            if (encodingOverride is TextEncoding ov && ov != TextEncoding.None)
            {
                encoding = ov;
            }
            else if (startOffset == 0)
            {
                pooledBuf = ArrayPool<byte>.Shared.Rent(sohPoolCap);
                int firstWant = (int)Math.Min((ulong)initialReadBytes, size);
                initialGot = LogIo.ReadAt(handle, 0, pooledBuf.AsSpan(0, firstWant));
                encoding = TextFormat.DetectStreamEncoding(pooledBuf.AsSpan(0, Math.Min(initialGot, 3)));
            }
            else
            {
                Span<byte> prefix = stackalloc byte[3];
                int n = RandomAccess.Read(handle, prefix, 0);
                encoding = TextFormat.DetectStreamEncoding(prefix[..n]);
            }

            int unit = TextFormat.UnitSize(encoding);
            int minFrag = TextFormat.MinimumFragmentBytes(encoding);
            int maxLine = Math.Clamp(limits.MaxLineBytes, minFrag, LogIo.MaxSearchLineBytes);
            int resultLimit = Math.Clamp(maxResults, 1, LogIo.MaxSearchResults);
            int previewLimit = Math.Clamp(limits.PreviewBytes, minFrag, LogIo.MaxPreviewBytes);

            cursor = TextFormat.AlignOffset(Math.Min(startOffset, size), encoding);
            pageStart = cursor;

            if (cursor >= size)
            {
                return new LogSearchPage([], cursor, 0, true, false, FileSize: size);
            }

            bool directUtf8Literal = plainCaseSensitiveLiteral
                && encoding is TextEncoding.Utf8 or TextEncoding.Utf8Bom
                && IsValidUtf8LiteralWithoutReplacement(query.Pattern);

            Span<byte> needleStack = stackalloc byte[256];
            int prefilterLen = TryBuildPrefilter(query, encoding, needleStack, out byte[]? heapNeedle, out bool prefilterIgnoreCaseAscii);
            ReadOnlySpan<byte> prefilterNeedle = heapNeedle is not null
                ? heapNeedle
                : needleStack[..prefilterLen];

            pooledBuf ??= ArrayPool<byte>.Shared.Rent(sohPoolCap);
            int bufferedLen = initialGot;
            int pos = 0;
            ulong bufFileOffset = cursor;
            bool needSkipPartial = cursor > 0 && !IsLineBoundary(handle, cursor, encoding);
            ulong lineFileStart = cursor;
            int lineCheckCounter = 0;
            bool didInitialRead = initialGot > 0;
            bool sohRefilled = false;
            bool finished;

            fixed (byte* pPooled = pooledBuf)
            {
                byte* sohPtr = pPooled;
                int sohCap = pooledBuf.Length;
                finished = RunSearchPass(
                    handle,
                    ref sohPtr,
                    ref sohCap,
                    isSohPhase: true,
                    initialReadBytes,
                    streamCap,
                    size,
                    encoding,
                    unit,
                    maxLine,
                    resultLimit,
                    previewLimit,
                    limits,
                    query.Pattern,
                    plainCaseSensitiveLiteral,
                    directUtf8Literal,
                    regex,
                    prefilterNeedle,
                    prefilterIgnoreCaseAscii,
                    hits,
                    ref charBuf,
                    ref bufFileOffset,
                    ref bufferedLen,
                    ref pos,
                    ref cursor,
                    ref pageStart,
                    ref needSkipPartial,
                    ref lineFileStart,
                    ref lineCheckCounter,
                    ref didInitialRead,
                    ref sohRefilled,
                    ref workLimitReached,
                    ref pendingLine,
                    ct);
            }

            if (!finished)
            {
                int lineStartRel = (int)(lineFileStart - bufFileOffset);
                int keep = bufferedLen - lineStartRel;
                bufFileOffset += (ulong)lineStartRel;
                pos -= lineStartRel;
                bufferedLen = keep;

                int effectiveCap = capAdjusted(maxLine, unit);
                int maxNeeded = Math.Min(effectiveCap + unit * 2, (int)Math.Min((ulong)effectiveCap + (ulong)(unit * 2), size - bufFileOffset));
                int nativeCap = Math.Max(streamCap + 8, Math.Min(maxNeeded + streamCap, Math.Max(bufferedLen + streamCap, streamCap + 8)));
                nativeBuf = (byte*)NativeMemory.Alloc((nuint)nativeCap);
                if (keep > 0)
                {
                    pooledBuf.AsSpan(lineStartRel, keep).CopyTo(new Span<byte>(nativeBuf, keep));
                }
                ArrayPool<byte>.Shared.Return(pooledBuf);
                pooledBuf = null;

                RunSearchPass(
                    handle,
                    ref nativeBuf,
                    ref nativeCap,
                    isSohPhase: false,
                    initialReadBytes,
                    streamCap,
                    size,
                    encoding,
                    unit,
                    maxLine,
                    resultLimit,
                    previewLimit,
                    limits,
                    query.Pattern,
                    plainCaseSensitiveLiteral,
                    directUtf8Literal,
                    regex,
                    prefilterNeedle,
                    prefilterIgnoreCaseAscii,
                    hits,
                    ref charBuf,
                    ref bufFileOffset,
                    ref bufferedLen,
                    ref pos,
                    ref cursor,
                    ref pageStart,
                    ref needSkipPartial,
                    ref lineFileStart,
                    ref lineCheckCounter,
                    ref didInitialRead,
                    ref sohRefilled,
                    ref workLimitReached,
                    ref pendingLine,
                    ct);
            }
        }
        finally
        {
            if (nativeBuf is not null)
            {
                NativeMemory.Free(nativeBuf);
            }
            if (pooledBuf is not null)
            {
                ArrayPool<byte>.Shared.Return(pooledBuf);
            }
            if (charBuf is not null)
            {
                ArrayPool<char>.Shared.Return(charBuf);
            }
        }

        return new LogSearchPage(
            hits,
            pendingLine ?? cursor,
            cursor - pageStart,
            cursor >= size && pendingLine is null,
            workLimitReached,
            pendingLine,
            size);
    }

    private static unsafe bool RunSearchPass(
        SafeFileHandle handle,
        ref byte* buf,
        ref int bufCapacity,
        bool isSohPhase,
        int initialReadBytes,
        int streamCap,
        ulong size,
        TextEncoding encoding,
        int unit,
        int maxLine,
        int resultLimit,
        int previewLimit,
        LogSearchLimits limits,
        string pattern,
        bool plainCaseSensitiveLiteral,
        bool directUtf8Literal,
        Regex? regex,
        ReadOnlySpan<byte> prefilterNeedle,
        bool prefilterIgnoreCaseAscii,
        List<LogSearchHit> hits,
        ref char[]? charBuf,
        ref ulong bufFileOffset,
        ref int bufferedLen,
        ref int pos,
        ref ulong cursor,
        ref ulong pageStart,
        ref bool needSkipPartial,
        ref ulong lineFileStart,
        ref int lineCheckCounter,
        ref bool didInitialRead,
        ref bool sohRefilled,
        ref bool workLimitReached,
        ref ulong? pendingLine,
        CancellationToken ct)
    {
        int effectiveCap = capAdjusted(maxLine, unit);

        while (cursor < size && hits.Count < resultLimit)
        {
            if (ct.IsCancellationRequested)
            {
                throw new LogCancelledException();
            }

            if (pos > 0 && pos >= bufferedLen)
            {
                bufFileOffset += (ulong)pos;
                bufferedLen = 0;
                pos = 0;
            }

            int lineStartRel = (int)(lineFileStart - bufFileOffset);
            int available = bufferedLen - pos;
            int skipAvail = bufferedLen - lineStartRel;
            bool allowPrefilter = !prefilterNeedle.IsEmpty && !needSkipPartial;
            bool tryPrefilterSkip = allowPrefilter
                && skipAvail >= 1024
                && maxLine >= skipAvail
                && (cursor - lineFileStart) == 0;

            if (tryPrefilterSkip && cursor + (ulong)skipAvail - pageStart < limits.MaxScannedBytes)
            {
                var window = new ReadOnlySpan<byte>(buf + lineStartRel, skipAvail);
                int skipEnd = FindSafeChunkSkipEnd(window, skipAvail, bufFileOffset + (ulong)bufferedLen < size);
                if (skipEnd > 0 && !ContainsNeedle(window[..skipEnd], prefilterNeedle, prefilterIgnoreCaseAscii))
                {
                    pos = lineStartRel + skipEnd;
                    cursor += (ulong)skipEnd;
                    lineFileStart = cursor;
                    continue;
                }
            }

            if (available > 0 && TryFindDelimiterEnsuringCrlf(
                    handle,
                    buf,
                    bufCapacity,
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
                if (contentLen > effectiveCap)
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
                    new ReadOnlySpan<byte>(buf + lineStartRel, contentLen),
                    lineFileStart,
                    lineEnd,
                    encoding,
                    pattern,
                    plainCaseSensitiveLiteral,
                    directUtf8Literal,
                    regex,
                    prefilterNeedle,
                    prefilterIgnoreCaseAscii,
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

            int currentLineBytes = (bufFileOffset + (ulong)bufferedLen) > lineFileStart
                ? (int)((bufFileOffset + (ulong)bufferedLen) - lineFileStart)
                : 0;

            if (currentLineBytes > effectiveCap)
            {
                throw new LogLineLimitException(lineFileStart, limits.MaxLineBytes);
            }

            int keep = bufferedLen - lineStartRel;
            ulong readOff = bufFileOffset + (ulong)bufferedLen;
            if (isSohPhase && readOff < size)
            {
                int sohUsableCap = bufCapacity - unit;
                bool canRefillSoh = !didInitialRead
                    || (keep < sohUsableCap && (size - readOff <= (ulong)(sohUsableCap - keep)
                        || (hits.Count > 0 && !sohRefilled && keep < sohUsableCap - 1024)));
                if (!canRefillSoh)
                {
                    return false;
                }
            }

            if (lineStartRel > 0)
            {
                if (keep > 0)
                {
                    new ReadOnlySpan<byte>(buf + lineStartRel, keep).CopyTo(new Span<byte>(buf, keep));
                }
                bufFileOffset += (ulong)lineStartRel;
                pos -= lineStartRel;
                bufferedLen = keep;
            }

            if (!isSohPhase)
            {
                int maxNeeded = Math.Min(effectiveCap + unit * 2, (int)Math.Min((ulong)effectiveCap + (ulong)(unit * 2), size - bufFileOffset));
                if (bufferedLen + streamCap > bufCapacity && bufCapacity < maxNeeded + streamCap)
                {
                    int nextSize = Math.Min(Math.Max(bufCapacity * 2, bufferedLen + streamCap), maxNeeded + streamCap);
                    if (nextSize > bufCapacity)
                    {
                        buf = (byte*)NativeMemory.Realloc(buf, (nuint)nextSize);
                        bufCapacity = nextSize;
                    }
                }
            }

            if (readOff >= size)
            {
                int eofLen = bufferedLen;
                if (eofLen > effectiveCap)
                {
                    eofLen = TrimToBoundary(new ReadOnlySpan<byte>(buf, effectiveCap), encoding, unit);
                    ulong endAtLimit = bufFileOffset + (ulong)eofLen;
                    if (endAtLimit < size)
                    {
                        throw new LogLineLimitException(lineFileStart, limits.MaxLineBytes);
                    }
                }
                else
                {
                    var bytes = new ReadOnlySpan<byte>(buf, eofLen);
                    int usable = TrimToBoundary(bytes, encoding, unit);
                    if (unit == 1 && usable < eofLen &&
                        Rune.DecodeFromUtf8(bytes[usable..], out _, out _) != OperationStatus.NeedMoreData) usable = eofLen;
                    if (usable < eofLen) pendingLine = needSkipPartial ? pageStart : lineFileStart;
                    eofLen = usable;
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
                            new ReadOnlySpan<byte>(buf, eofLen),
                            lineFileStart,
                            lineEnd,
                            encoding,
                            pattern,
                            plainCaseSensitiveLiteral,
                            directUtf8Literal,
                            regex,
                            prefilterNeedle,
                            prefilterIgnoreCaseAscii,
                            previewLimit,
                            hits,
                            ref charBuf);
                        cursor = lineEnd;
                    }
                }
                break;
            }

            int wantRead;
            if (isSohPhase)
            {
                int capLimit = !didInitialRead ? initialReadBytes : (bufCapacity - unit);
                int spaceLeft = capLimit - bufferedLen;
                wantRead = (int)Math.Min((ulong)Math.Max(0, spaceLeft), size - readOff);
                didInitialRead = true;
                if (capLimit != initialReadBytes)
                {
                    sohRefilled = true;
                }
            }
            else
            {
                int spaceLeft = bufCapacity - bufferedLen;
                wantRead = (int)Math.Min((ulong)Math.Min(streamCap, spaceLeft), size - readOff);
            }

            int got = wantRead > 0 ? LogIo.ReadAt(handle, readOff, new Span<byte>(buf + bufferedLen, wantRead)) : 0;
            if (got <= 0)
            {
                if (bufferedLen > 0 && !needSkipPartial)
                {
                    int eofLen = unit == 2 && (bufferedLen % 2) != 0 ? bufferedLen - 1 : bufferedLen;
                    ulong lineEnd = bufFileOffset + (ulong)eofLen;
                    EvaluateCandidateLine(
                        new ReadOnlySpan<byte>(buf, eofLen),
                        lineFileStart,
                        lineEnd,
                        encoding,
                        pattern,
                        plainCaseSensitiveLiteral,
                        directUtf8Literal,
                        regex,
                        prefilterNeedle,
                        prefilterIgnoreCaseAscii,
                        previewLimit,
                        hits,
                        ref charBuf);
                    cursor = lineEnd;
                }
                break;
            }

            pos = bufferedLen;
            bufferedLen += got;
        }

        return true;
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

    private static unsafe bool TryFindDelimiterEnsuringCrlf(
        SafeFileHandle handle,
        byte* buf,
        int bufCapacity,
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

        if (!LogIo.TryFindNextDelimiter(new ReadOnlySpan<byte>(buf, bufferedLen), scanStart, encoding, out dStart, out dLen))
        {
            return false;
        }

        // If delimiter is CR right at the buffer end before EOF, pull next unit to check CRLF pair.
        int endAfterFirst = dStart + unit;
        if (endAfterFirst == bufferedLen && bufFileOffset + (ulong)bufferedLen < fileSize && bufferedLen + unit <= bufCapacity)
        {
            if (LogIo.IsCarriageReturnUnit(new ReadOnlySpan<byte>(buf + dStart, unit), encoding))
            {
                int extra = LogIo.ReadAt(handle, bufFileOffset + (ulong)bufferedLen, new Span<byte>(buf + bufferedLen, unit));
                if (extra > 0)
                {
                    bufferedLen += extra;
                    if (bufferedLen >= dStart + unit * 2 && LogIo.IsNewlineUnit(new ReadOnlySpan<byte>(buf + dStart + unit, unit), encoding))
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
        string pattern,
        bool plainCaseSensitiveLiteral,
        bool directUtf8Literal,
        Regex? regex,
        ReadOnlySpan<byte> prefilterNeedle,
        bool prefilterIgnoreCaseAscii,
        int previewLimit,
        List<LogSearchHit> hits,
        ref char[]? charBuf)
    {
        if (directUtf8Literal)
        {
            ReadOnlySpan<byte> body = lineStart == 0 && encoding == TextEncoding.Utf8Bom && content is [0xEF, 0xBB, 0xBF, ..]
                ? content[3..]
                : content;
            if (body.IndexOf(prefilterNeedle) < 0)
            {
                return;
            }

            if ((long)body.Length * 3 <= previewLimit)
            {
                string directPreview = TextFormat.DecodeLossy(body, TextEncoding.Utf8, atFileStart: false);
                hits.Add(new LogSearchHit(lineStart, lineEnd, directPreview));
                return;
            }

            ReadOnlySpan<char> directChars = DecodeCandidateChars(body, TextEncoding.Utf8, atFileStart: false, ref charBuf);
            hits.Add(new LogSearchHit(lineStart, lineEnd, TruncatePreview(directChars, previewLimit)));
            return;
        }

        if (plainCaseSensitiveLiteral)
        {
            ReadOnlySpan<char> chars = DecodeCandidateChars(content, encoding, lineStart == 0, ref charBuf);
            if (chars.IndexOf(pattern.AsSpan(), StringComparison.Ordinal) >= 0)
            {
                hits.Add(new LogSearchHit(lineStart, lineEnd, TruncatePreview(chars, previewLimit)));
            }
            return;
        }

        if (!prefilterNeedle.IsEmpty)
        {
            ReadOnlySpan<byte> body = lineStart == 0 && encoding == TextEncoding.Utf8Bom && content is [0xEF, 0xBB, 0xBF, ..]
                ? content[3..]
                : content;
            if (!ContainsNeedle(body, prefilterNeedle, prefilterIgnoreCaseAscii))
            {
                return;
            }
        }

        ReadOnlySpan<char> regexChars = DecodeCandidateChars(content, encoding, lineStart == 0, ref charBuf);
        if (regex!.IsMatch(regexChars))
        {
            hits.Add(new LogSearchHit(lineStart, lineEnd, TruncatePreview(regexChars, previewLimit)));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<char> DecodeCandidateChars(
        ReadOnlySpan<byte> content,
        TextEncoding encoding,
        bool atFileStart,
        ref char[]? charBuf)
    {
        int maxChars = TextFormat.GetMaxLossyCharCount(content.Length, encoding);
        if (charBuf is null || maxChars > charBuf.Length)
        {
            if (charBuf is not null)
            {
                ArrayPool<char>.Shared.Return(charBuf);
            }
            charBuf = ArrayPool<char>.Shared.Rent(Math.Max(maxChars, 4096));
        }

        int charLen = TextFormat.DecodeLossyChars(content, encoding, atFileStart, charBuf);
        return charBuf.AsSpan(0, charLen);
    }

    private static bool IsValidUtf8LiteralWithoutReplacement(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\uFFFD')
            {
                return false;
            }
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    return false;
                }
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }
        return true;
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

    private static bool ContainsNeedle(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, bool asciiIgnoreCase)
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

    private static int TryBuildPrefilter(
        LogSearchQuery query,
        TextEncoding encoding,
        Span<byte> stackBuf,
        out byte[]? heapNeedle,
        out bool asciiIgnoreCase)
    {
        heapNeedle = null;
        asciiIgnoreCase = false;
        if (encoding is not (TextEncoding.Utf8 or TextEncoding.Utf8Bom) || string.IsNullOrEmpty(query.Pattern))
        {
            return 0;
        }

        string? literal = query.Regex ? ExtractSafeRegexLiteral(query.Pattern) : query.Pattern;
        if (string.IsNullOrEmpty(literal) || !IsValidUtf8LiteralWithoutReplacement(literal))
        {
            return 0;
        }

        if (query.CaseSensitive)
        {
            int byteCount = Encoding.UTF8.GetByteCount(literal);
            if (byteCount <= stackBuf.Length)
            {
                return Encoding.UTF8.GetBytes(literal, stackBuf);
            }

            heapNeedle = Encoding.UTF8.GetBytes(literal);
            return heapNeedle.Length;
        }

        if (Ascii.IsValid(literal))
        {
            asciiIgnoreCase = true;
            if (literal.Length <= stackBuf.Length)
            {
                for (int i = 0; i < literal.Length; i++)
                {
                    char ch = literal[i];
                    stackBuf[i] = (byte)(ch >= 'A' && ch <= 'Z' ? ch + 32 : ch);
                }
                return literal.Length;
            }

            heapNeedle = Encoding.ASCII.GetBytes(literal.ToLowerInvariant());
            return heapNeedle.Length;
        }

        return 0;
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
        if ((long)text.Length * 3 <= maxUtf8Bytes)
        {
            return new string(text);
        }

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

public static partial class LogIo
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

    private const uint FileGenericRead = 0x00120089u;
    private const uint FileShareAll = 0x00000007u;
    private const uint FileFlagSequentialScan = 0x08000000u;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint ReOpenFile(nint hOriginalFile, uint dwDesiredAccess, uint dwShareMode, uint dwFlagsAndAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(nint hObject);

    internal static SafeFileHandle? TryReOpenReadHandle(SafeFileHandle original)
    {
        if (!OperatingSystem.IsWindows() || original.IsInvalid || original.IsClosed)
        {
            return null;
        }

        bool addedRef = false;
        nint raw = 0;
        try
        {
            original.DangerousAddRef(ref addedRef);
            raw = ReOpenFile(original.DangerousGetHandle(), FileGenericRead, FileShareAll, FileFlagSequentialScan);
            if (raw == 0 || raw == -1)
            {
                return null;
            }
            var reopened = new SafeFileHandle(raw, ownsHandle: true);
            raw = 0;
            return reopened;
        }
        catch
        {
            if (raw != 0 && raw != -1)
            {
                CloseHandle(raw);
            }
            return null;
        }
        finally
        {
            if (addedRef)
            {
                original.DangerousRelease();
            }
        }
    }

    internal static SafeFileHandle OpenReadHandle(string path, FileOptions fileOptions)
    {
        try
        {
            return File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                fileOptions);
        }
        catch (UnauthorizedAccessException) when (Directory.Exists(path))
        {
            throw new InvalidDataException("Выберите обычный файл.");
        }
        catch (IOException) when (Directory.Exists(path))
        {
            throw new InvalidDataException("Выберите обычный файл.");
        }
    }

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

    public static byte[] ReadMinimapWidths(
        string path,
        ulong start = 0,
        int bands = 32,
        TextEncoding? encodingOverride = null,
        CancellationToken ct = default)
    {
        using var reader = LogReader.Open(path, encodingOverride);
        return reader.ReadMinimapWidths(start, null, bands, ct);
    }

    public static byte[] ReadMinimapWidths(
        FileStream stream,
        TextEncoding encoding,
        ulong start = 0,
        int bands = 32,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ReadMinimapWidths(stream.SafeFileHandle, (ulong)stream.Length, encoding, null, start, bands, ct);
    }

    internal static byte[] ReadMinimapWidths(
        SafeFileHandle handle,
        ulong fileLength,
        TextEncoding encoding,
        LogChunkCache? cache,
        ulong start,
        int bands,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int bomLen = DetectActualBomLength(handle, fileLength, encoding);
        start = Math.Max(start, (ulong)bomLen);
        if (start >= fileLength) return [];

        int unit = TextFormat.UnitSize(encoding);
        if (unit == 2) start += (start - (ulong)bomLen) % 2;
        ulong units = (fileLength - start) / (ulong)unit;
        if (units == 0) return [];

        const int minimapColumns = 32;
        int count = Math.Clamp(bands, 1, 256);
        var widths = new byte[(int)Math.Min(units, (ulong)count)];
        int intervals = Math.Max(1, widths.Length - 1);
        Span<byte> buffer = stackalloc byte[minimapColumns * 4 + 3];
        Span<char> chars = stackalloc char[minimapColumns * 4 + 4];

        for (int i = 0; i < widths.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            ulong unitIdx = (ulong)((UInt128)i * (units - 1) / (uint)intervals);
            ulong offset = start + unitIdx * (ulong)unit;
            int want = (int)Math.Min((ulong)buffer.Length, fileLength - offset);
            int read = cache is not null
                ? cache.Read(handle, fileLength, offset, buffer[..want])
                : ReadAt(handle, offset, buffer[..want]);

            ReadOnlySpan<byte> bytes = buffer[..(read - read % unit)];
            if (encoding is TextEncoding.Utf8 or TextEncoding.Utf8Bom && offset > (ulong)bomLen)
            {
                while (!bytes.IsEmpty && (bytes[0] & 0xC0) == 0x80) bytes = bytes[1..];
            }
            if (TryFindNextDelimiter(bytes, 0, encoding, out int dStart, out _))
            {
                bytes = bytes[..dStart];
            }
            int charCount = TextFormat.DecodeLossyChars(bytes, encoding, atFileStart: false, chars);
            widths[i] = (byte)Math.Min(minimapColumns, charCount);
        }

        return widths;
    }

    public static PageEditSnapshot ReadPageEdit(
        string path,
        LogPage page,
        TextEncoding encoding,
        FileStamp expectedStamp,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ulong from = page.Rows.Count == 0 ? page.NextOffset : page.Rows[0].Start;
        ulong to = Math.Max(from, page.NextOffset);
        return ReadPageEdit(path, from, to, encoding, expectedStamp, ct);
    }

    public static PageEditSnapshot ReadPageEdit(
        string path,
        ulong from,
        ulong to,
        TextEncoding encoding,
        FileStamp expectedStamp,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectedStamp);
        ct.ThrowIfCancellationRequested();

        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (LogFile.StampOf(path, handle) != expectedStamp)
        {
            throw ChangedSincePageRead();
        }

        if (from == 0)
        {
            int bom = DetectActualBomLength(handle, expectedStamp.Length, encoding);
            if (bom > 0 && to >= (ulong)bom)
            {
                from = (ulong)bom;
            }
        }

        if (to < from) to = from;
        if (to > expectedStamp.Length)
        {
            throw ChangedSincePageRead();
        }

        ulong len = to - from;
        if (len > (ulong)MaxPageBytes)
        {
            throw new IOException($"Размер страницы превышает допустимый лимит ({MaxPageBytes} байт).");
        }

        var originalBytes = new byte[checked((int)len)];
        if (originalBytes.Length > 0 && ReadAt(handle, from, originalBytes) != originalBytes.Length)
        {
            throw ChangedSincePageRead();
        }

        ct.ThrowIfCancellationRequested();
        if (LogFile.StampOf(path, handle) != expectedStamp)
        {
            throw ChangedSincePageRead();
        }

        string originalText = GetStrictPageEncoding(encoding).GetString(originalBytes);
        return new PageEditSnapshot(from, to, originalText, originalBytes, encoding, expectedStamp);
    }

    public static FileStamp SavePageEdit(
        string path,
        PageEditSnapshot edit,
        string newText,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(newText);
        ct.ThrowIfCancellationRequested();

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("Укажите папку файла.");
        byte[] replacement = GetStrictPageEncoding(edit.Encoding).GetBytes(newText);
        string tempPath = Path.Combine(directory, $".nettext-page-{Guid.NewGuid():N}.pending");

        const int copyBufferSize = 1024 * 1024;
        byte[] copyBuffer = ArrayPool<byte>.Shared.Rent(copyBufferSize);

        try
        {
            using (var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                copyBufferSize, FileOptions.SequentialScan))
            {
                if (LogFile.StampOf(fullPath, source.SafeFileHandle) != edit.Stamp)
                {
                    throw ChangedSincePageRead();
                }

                int origLen = edit.OriginalBytes.Length;
                if ((ulong)source.Length < edit.To || (ulong)origLen != edit.To - edit.From)
                {
                    throw ChangedSincePageRead();
                }

                if (origLen > 0)
                {
                    byte[] verifyBuf = origLen <= copyBuffer.Length ? copyBuffer : ArrayPool<byte>.Shared.Rent(origLen);
                    try
                    {
                        source.Position = (long)edit.From;
                        source.ReadExactly(verifyBuf.AsSpan(0, origLen));
                        if (!verifyBuf.AsSpan(0, origLen).SequenceEqual(edit.OriginalBytes))
                        {
                            throw ChangedSincePageRead();
                        }
                    }
                    finally
                    {
                        if (!ReferenceEquals(verifyBuf, copyBuffer))
                        {
                            ArrayPool<byte>.Shared.Return(verifyBuf);
                        }
                    }
                }

                using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, copyBufferSize))
                {
                    source.Position = 0;
                    CopyStreamRange(source, output, (long)edit.From, copyBuffer, ct);
                    if (replacement.Length > 0)
                    {
                        output.Write(replacement);
                    }
                    source.Position = (long)edit.To;
                    CopyStreamRange(source, output, (long)(edit.Stamp.Length - edit.To), copyBuffer, ct);
                    output.Flush(flushToDisk: true);
                }

                ct.ThrowIfCancellationRequested();
                if (LogFile.StampOf(fullPath, source.SafeFileHandle) != edit.Stamp)
                {
                    throw ChangedSincePageRead();
                }
            }

            try
            {
                File.Replace(tempPath, fullPath, destinationBackupFileName: null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                File.Move(tempPath, fullPath, overwrite: true);
            }

            using var updatedHandle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return LogFile.StampOf(fullPath, updatedHandle);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer);
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void CopyStreamRange(FileStream source, FileStream output, long remaining, byte[] buffer, CancellationToken ct)
    {
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(remaining, buffer.Length);
            source.ReadExactly(buffer.AsSpan(0, want));
            output.Write(buffer.AsSpan(0, want));
            remaining -= want;
        }
    }

    private static int DetectActualBomLength(SafeFileHandle handle, ulong fileLength, TextEncoding encoding)
    {
        int expected = TextFormat.BomLength(encoding);
        if (expected == 0 || fileLength < (ulong)expected) return 0;

        Span<byte> prefix = stackalloc byte[3];
        int read = ReadAt(handle, 0, prefix[..expected]);
        if (read < expected) return 0;

        return encoding switch
        {
            TextEncoding.Utf8Bom when prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF => 3,
            TextEncoding.Utf16Le when prefix[0] == 0xFF && prefix[1] == 0xFE => 2,
            TextEncoding.Utf16Be when prefix[0] == 0xFE && prefix[1] == 0xFF => 2,
            _ => 0,
        };
    }

    private static Encoding GetStrictPageEncoding(TextEncoding encoding)
    {
        if (encoding == TextEncoding.Windows1251)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        return encoding switch
        {
            TextEncoding.Utf16Le => new UnicodeEncoding(false, false, true),
            TextEncoding.Utf16Be => new UnicodeEncoding(true, false, true),
            _ => new UTF8Encoding(false, true),
        };
    }

    private static IOException ChangedSincePageRead() =>
        new("Файл изменился на диске. Обновите страницу перед сохранением.");
}

public sealed record PageEditSnapshot(
    ulong From,
    ulong To,
    string OriginalText,
    byte[] OriginalBytes,
    TextEncoding Encoding,
    FileStamp Stamp);
