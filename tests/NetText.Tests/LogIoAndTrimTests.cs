using System.Text;
using NetText.Core;
using Xunit;

namespace NetText.Tests;

public sealed class LogIoAndTrimTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nettext-log-{Guid.NewGuid():N}");

    public LogIoAndTrimTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void DetectsUtfBomsAndDecodesRows()
    {
        var p1 = Path.Combine(_dir, "bom.txt");
        File.WriteAllBytes(p1, [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', (byte)'\r', (byte)'\n', (byte)'w', (byte)'o', (byte)'r', (byte)'l', (byte)'d']);
        using (var r = LogReader.Open(p1, null))
        {
            Assert.Equal(TextEncoding.Utf8Bom, r.Encoding);
            var page = r.RenderPage(0, PageOptions.Default);
            Assert.Equal(2, page.Rows.Count);
            Assert.Equal("hello", page.Rows[0].Text);
            Assert.True(page.Rows[0].Terminated);
            Assert.Equal("world", page.Rows[1].Text);
            Assert.True(page.Eof);
        }

        var p2 = Path.Combine(_dir, "u16.txt");
        File.WriteAllBytes(p2, [0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0, 10, 0]);
        using (var r2 = LogReader.Open(p2, null))
        {
            Assert.Equal(TextEncoding.Utf16Le, r2.Encoding);
            var page = r2.RenderPage(0, PageOptions.Default);
            Assert.Equal("hi", page.Rows[0].Text);
        }
    }

    [Fact]
    public void RendersLongLinesAndPreservesUtf8Fragments()
    {
        var p = Path.Combine(_dir, "emoji.txt");
        File.WriteAllBytes(p, Encoding.UTF8.GetBytes("ab🙂z\r\nnext"));
        using var r = LogReader.Open(p, null);
        var page = r.RenderPage(0, new PageOptions(10, 5));
        Assert.Equal("ab", page.Rows[0].Text);
        Assert.True(page.Rows[0].Continues);
        Assert.Equal("🙂z", page.Rows[1].Text);
        Assert.True(page.Rows[1].Terminated);
        Assert.Equal("next", page.Rows[2].Text);
    }

    [Fact]
    public void SparseIndexCountsCrlfOnceAcross64KBoundary()
    {
        var p = Path.Combine(_dir, "crlf64k.txt");
        var bytes = new List<byte>(LogIo.BlockSize + 32);
        bytes.AddRange(Enumerable.Repeat((byte)'x', LogIo.BlockSize - 1));
        bytes.AddRange(Encoding.ASCII.GetBytes("\r\na\nb\n"));
        File.WriteAllBytes(p, [.. bytes]);

        using var r = LogReader.Open(p, null);
        r.AdvanceIndex(LogIo.BlockSize, CancellationToken.None);
        Assert.Equal(1UL, r.Index.LineCount);

        r.AdvanceIndex(16, CancellationToken.None);
        Assert.Equal(4UL, r.Index.LineCount);
        Assert.Equal(new SparseLine(2, (ulong)LogIo.BlockSize + 1), r.Index.Entries[1]);
    }

    [Fact]
    public void SearchBatchesAndCrLfBoundaryAndLineLimitsWork()
    {
        var p = Path.Combine(_dir, "search.txt");
        File.WriteAllBytes(p, Encoding.UTF8.GetBytes("111\n222\nhit\n"));
        var q = new LogSearchQuery("hit", false, false, false);
        var p1 = LogSearch.SearchPage(p, null, q, 0, 10, new LogSearchLimits(5, 1024, 256), CancellationToken.None);
        Assert.True(p1.WorkLimitReached);
        Assert.Equal(8UL, p1.NextOffset);
        Assert.Empty(p1.Hits);

        var p2 = LogSearch.SearchPage(p, null, q, p1.NextOffset, 10, new LogSearchLimits(512, 1024, 256), CancellationToken.None);
        Assert.Single(p2.Hits);
        Assert.Equal(8UL, p2.Hits[0].LineStart);

        var longPath = Path.Combine(_dir, "long.txt");
        File.WriteAllText(longPath, "0123456789\n");
        Assert.Throws<LogLineLimitException>(() =>
            LogSearch.SearchPage(longPath, null, new LogSearchQuery("1", false, false, false), 0, 10, new LogSearchLimits(1024, 4, 256), CancellationToken.None));

        var crlfSearch = Path.Combine(_dir, "crlf-search.txt");
        var data = new byte[LogIo.BlockSize * 2 - 1 + 10];
        Array.Fill(data, (byte)'x', 0, LogIo.BlockSize * 2 - 1);
        Encoding.ASCII.GetBytes("\r\nneedle\r\n").CopyTo(data, LogIo.BlockSize * 2 - 1);
        File.WriteAllBytes(crlfSearch, data);
        var hitPage = LogSearch.SearchPage(crlfSearch, null, new LogSearchQuery("needle", false, false, false), 0, 10, LogSearchLimits.Default, CancellationToken.None);
        Assert.Single(hitPage.Hits);
        Assert.Equal((ulong)(LogIo.BlockSize * 2 + 1), hitPage.Hits[0].LineStart);
    }

    [Fact]
    public void PageBeforeReachesEndAndHandlesLargeLines()
    {
        var p = Path.Combine(_dir, "lines.txt");
        File.WriteAllText(p, "one\ntwo\nthree\nfour\nfive");
        using var r = LogReader.Open(p, null);
        var page = r.PageBefore(r.Size, new PageOptions(2, 1024));
        Assert.Equal(new[] { "four", "five" }, page.Rows.Select(x => x.Text).ToArray());
        Assert.Equal(r.Size, page.NextOffset);
    }

    [Fact]
    public void LogFileObservesAppendRewriteTruncationAndReplacement()
    {
        var p = Path.Combine(_dir, "obs.log");
        File.WriteAllText(p, "first line\n");
        var (obs1, ch1) = LogFile.Observe(p, null);
        Assert.Equal(LogChangeKind.Replaced, ch1);

        var (_, chSame) = LogFile.Observe(p, obs1);
        Assert.Equal(LogChangeKind.Same, chSame);

        File.AppendAllText(p, "second!\n");
        var (obs2, chApp) = LogFile.Observe(p, obs1);
        Assert.Equal(LogChangeKind.Appended, chApp);

        File.WriteAllText(p, "completely different!!");
        var (obs3, chRew) = LogFile.Observe(p, obs2);
        Assert.Equal(LogChangeKind.Rewritten, chRew);

        File.WriteAllText(p, "");
        var (_, chTrunc) = LogFile.Observe(p, obs3);
        Assert.Equal(LogChangeKind.Truncated, chTrunc);
    }

    [Fact]
    public void LogTrimBoundariesAndBackupRecoveryWork()
    {
        var p = Path.Combine(_dir, "trim.log");
        File.WriteAllBytes(p, Encoding.UTF8.GetBytes("line one\nline two\nline three\n"));
        var plan = LogTrim.PrepareWithProgress(p, 40, TextEncoding.Utf8, CancellationToken.None);
        Assert.Equal(18UL, plan.BoundaryOffset);
        Assert.Equal(18UL, plan.RemovableBytes);

        var outcome = LogTrim.Trim(p, plan, CancellationToken.None);
        Assert.Equal("line three\n", File.ReadAllText(p));
        Assert.True(File.Exists(outcome.BackupPath));
        Assert.Equal("line one\nline two\nline three\n", File.ReadAllText(outcome.BackupPath));

        var pBom = Path.Combine(_dir, "trim-bom.log");
        byte[] bomData = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("first\nsecond\nthird\n")];
        File.WriteAllBytes(pBom, bomData);
        var planBom = LogTrim.PrepareWithProgress(pBom, 70, TextEncoding.Utf8Bom, CancellationToken.None);
        var outBom = LogTrim.Trim(pBom, planBom, CancellationToken.None);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("third\n")], File.ReadAllBytes(pBom));
        Assert.Equal(13UL, outBom.RemovedBytes);

        var pFail = Path.Combine(_dir, "trim-fail.log");
        var origBytes = Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\ndelta\n");
        File.WriteAllBytes(pFail, origBytes);
        var failPlan = LogTrim.PrepareWithProgress(pFail, 50, TextEncoding.Utf8, CancellationToken.None);
        var ex = Assert.Throws<IOException>(() => LogTrim.TrimInternal(
            pFail,
            failPlan,
            CancellationToken.None,
            null,
            new TrimHooks { OnShiftChunkWritten = _ => throw new IOException("simulated shift crash") }));
        Assert.Contains("исходное содержимое восстановлено", ex.Message);
        Assert.Equal(origBytes, File.ReadAllBytes(pFail));
    }

    [Fact]
    public void PageBeforeReachesTargetWhenLongLineFragmentsExceedRowLimit()
    {
        var p = Path.Combine(_dir, "long-tail.log");
        string longLine = new('a', 300);
        File.WriteAllText(p, $"{longLine}\npenultimate\nlast");
        using var r = LogReader.Open(p, null);
        var page = r.PageBefore(r.Size, new PageOptions(2, 64));
        Assert.Equal(new[] { "penultimate", "last" }, page.Rows.Select(x => x.Text).ToArray());
        Assert.Equal(r.Size, page.NextOffset);
    }

    [Fact]
    public void AssemblePageTextJoinsLongFragmentsWithoutInsertingNewlines()
    {
        var pJson = Path.Combine(_dir, "long-json.log");
        string json = $"{{\"value\":\"{new string('x', 5000)}\"}}";
        string contentJson = $"{json}\nnext";
        File.WriteAllText(pJson, contentJson);
        using (var r = LogReader.Open(pJson, null))
        {
            var page = r.RenderPage(0, new PageOptions(8, 2048));
            Assert.True(page.Rows.Count > 1);
            Assert.Equal(contentJson, NetText.UI.LogViewerState.AssemblePageText(page.Rows));
        }

        var pEmoji = Path.Combine(_dir, "utf8-frag.log");
        string emojis = $"{string.Concat(Enumerable.Repeat("🙂", 1200))}\nnext";
        File.WriteAllText(pEmoji, emojis);
        using (var r2 = LogReader.Open(pEmoji, null))
        {
            var page2 = r2.RenderPage(0, new PageOptions(8, 2048));
            Assert.Equal(emojis, NetText.UI.LogViewerState.AssemblePageText(page2.Rows));
        }
    }

    [Fact]
    public void AnchoredLineStartAtFindsLineFromSparseAnchor()
    {
        var p = Path.Combine(_dir, "anchored.log");
        var line = new string('z', 200) + "\n";
        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++) sb.Append(line);
        File.WriteAllText(p, sb.ToString());

        using var r = LogReader.Open(p, null);
        r.Index.Advance(r.Handle, r.Encoding, r.Size, CancellationToken.None);
        ulong target = r.Size / 2 + 15;
        var anchor = r.Index.FindAnchor(target);
        Assert.True(anchor.Offset > 0);
        Assert.True(target - anchor.Offset <= LogIo.MaxBackwardScan);
        ulong start = LineIndex.LineStartAtFromAnchor(r.Handle, r.Encoding, target, r.Size, anchor);
        Assert.True(start <= target);
        Assert.True(start % (ulong)line.Length == 0);
    }

    [Fact]
    public void LogChunkCacheServesHitsEvictsLruAndInvalidates()
    {
        var p = Path.Combine(_dir, "chunk-cache.log");
        byte[] payload = new byte[LogChunkCache.ChunkSize * 3 + 128];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('A' + (i % 26));
        File.WriteAllBytes(p, payload);

        var cache = new LogChunkCache(capacity: 2);
        using var r = LogReader.Open(p, null, cache);
        Span<byte> buf = stackalloc byte[32];

        Assert.Equal(32, cache.Read(r.Handle, r.Size, 100, buf));
        Assert.Equal(1, cache.Count);
        Assert.Equal(payload.AsSpan(100, 32).ToArray(), buf.ToArray());

        Assert.Equal(32, cache.Read(r.Handle, r.Size, (ulong)LogChunkCache.ChunkSize + 10, buf));
        Assert.Equal(2, cache.Count);
        Assert.Equal(32, cache.Read(r.Handle, r.Size, (ulong)(LogChunkCache.ChunkSize * 2) + 5, buf));
        Assert.Equal(2, cache.Count);

        cache.InvalidateFrom((ulong)LogChunkCache.ChunkSize * 2);
        Assert.Equal(1, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void LogViewportHelpersExpandTabsAndResolveWordBounds()
    {
        Assert.Equal("ab  c", NetText.UI.LogViewport.ExpandTabs("ab\tc", 4));
        Assert.Equal("    x", NetText.UI.LogViewport.ExpandTabs("\tx", 4));
        Assert.Equal(4, NetText.UI.LogViewport.RawColToExpanded("ab\tc", 3, 4));
        Assert.Equal(3, NetText.UI.LogViewport.ExpandedColToRaw("ab\tc", 4, 4));
        Assert.Equal((3, 9), NetText.UI.LogViewport.WordBoundsAt("ok лог_12!", 5));
        Assert.Equal((2, 4), NetText.UI.LogViewport.WordBoundsAt("a 🙂 b", 3));
    }

    [Fact]
    public void SparseIndexCountsCrlfOnceAcrossSingleCallChunkStepBoundary()
    {
        foreach (int chunkStep in new[] { LogIo.BlockSize * 4, LogIo.BlockSize * 16 })
        {
            // Minimal chunkStep+2 reproducer: CR at exact chunkStep, LF at chunkStep + 1
            var splitEofPath = Path.Combine(_dir, $"crlf-{chunkStep}-split-eof.txt");
            var bytesEof = new byte[chunkStep + 2];
            Array.Fill(bytesEof, (byte)'x', 0, chunkStep);
            bytesEof[chunkStep] = (byte)'\r';
            bytesEof[chunkStep + 1] = (byte)'\n';
            File.WriteAllBytes(splitEofPath, bytesEof);

            using (var readerEof = LogReader.Open(splitEofPath, null))
            {
                readerEof.Index.Advance(readerEof.Handle, readerEof.Encoding, readerEof.Size, CancellationToken.None);
                Assert.Equal(2UL, readerEof.Index.LineCount);
            }

            // Same chunkStep split followed by trailing line content
            var splitBodyPath = Path.Combine(_dir, $"crlf-{chunkStep}-split-body.txt");
            var bytesBody = new byte[chunkStep + 3];
            Array.Fill(bytesBody, (byte)'x', 0, chunkStep);
            bytesBody[chunkStep] = (byte)'\r';
            bytesBody[chunkStep + 1] = (byte)'\n';
            bytesBody[chunkStep + 2] = (byte)'y';
            File.WriteAllBytes(splitBodyPath, bytesBody);

            using (var readerBody = LogReader.Open(splitBodyPath, null))
            {
                readerBody.Index.Advance(readerBody.Handle, readerBody.Encoding, readerBody.Size, CancellationToken.None);
                Assert.Equal(2UL, readerBody.Index.LineCount);
                Assert.Equal((ulong)(chunkStep + 2), readerBody.Index.LineStartAt(readerBody.Handle, readerBody.Encoding, (ulong)(chunkStep + 2), readerBody.Size));
            }
        }

        const int driftSpan = LogIo.BlockSize * 16 * 3;
        // Multi-chunk 6-byte CRLF pattern hitting chunk boundary drift across multiple 1 MiB / 256 KiB steps
        var shortPattern = "abcd\r\n"u8.ToArray();
        var driftPath = Path.Combine(_dir, "crlf-drift-multi-chunk.txt");
        int shortRepeats = driftSpan / shortPattern.Length + 7;
        var driftBytes = new byte[shortRepeats * shortPattern.Length];
        for (int i = 0; i < shortRepeats; i++)
        {
            Buffer.BlockCopy(shortPattern, 0, driftBytes, i * shortPattern.Length, shortPattern.Length);
        }
        File.WriteAllBytes(driftPath, driftBytes);

        using (var readerDrift = LogReader.Open(driftPath, null))
        {
            readerDrift.Index.Advance(readerDrift.Handle, readerDrift.Encoding, readerDrift.Size, CancellationToken.None);
            Assert.Equal((ulong)shortRepeats + 1UL, readerDrift.Index.LineCount);
        }

        // CoreBench-like repeating 84-byte CRLF pattern + LineStartAtFromAnchor across > LogIo.BlockSize
        var pattern = Encoding.UTF8.GetBytes("2026-11-16T21:30:00Z INFO rusttext::bench line=12345 message=hello_world_test_data\r\n");
        var multiPath = Path.Combine(_dir, "crlf-multi-chunk.txt");
        int repeats = (LogIo.BlockSize * 20) / pattern.Length + 17;
        var multi = new byte[repeats * pattern.Length];
        for (int i = 0; i < repeats; i++)
        {
            Buffer.BlockCopy(pattern, 0, multi, i * pattern.Length, pattern.Length);
        }
        File.WriteAllBytes(multiPath, multi);

        using (var reader2 = LogReader.Open(multiPath, null))
        {
            reader2.Index.Advance(reader2.Handle, reader2.Encoding, reader2.Size, CancellationToken.None);
            Assert.Equal((ulong)repeats + 1UL, reader2.Index.LineCount);

            var anchor = new SparseLine(1, 0);
            ulong targetOffset = (ulong)LogIo.BlockSize + (ulong)pattern.Length * 2 + 10;
            ulong lineStart = LineIndex.LineStartAtFromAnchor(reader2.Handle, reader2.Encoding, targetOffset, reader2.Size, anchor);
            Assert.Equal(0UL, lineStart % (ulong)pattern.Length);
        }

        // Vector128 (16-byte) and Vector256 (32-byte) lane-boundary CRLF + incremental batch boundaries
        foreach (int lane in new[] { 16, 32 })
        {
            var simdPath = Path.Combine(_dir, $"crlf-simd-{lane}.txt");
            var simdBytes = new byte[lane * 3 + 5];
            Array.Fill(simdBytes, (byte)'a');
            simdBytes[lane - 1] = (byte)'\r';
            simdBytes[lane] = (byte)'\n';
            simdBytes[lane * 2 - 1] = (byte)'\r';
            simdBytes[lane * 2] = (byte)'\n';
            File.WriteAllBytes(simdPath, simdBytes);

            using var singleCallReader = LogReader.Open(simdPath, null);
            singleCallReader.Index.Advance(singleCallReader.Handle, singleCallReader.Encoding, singleCallReader.Size, CancellationToken.None);
            Assert.Equal(3UL, singleCallReader.Index.LineCount);

            using var batchReader = LogReader.Open(simdPath, null);
            batchReader.Index.Advance(batchReader.Handle, batchReader.Encoding, (ulong)lane, CancellationToken.None);
            batchReader.Index.Advance(batchReader.Handle, batchReader.Encoding, batchReader.Size, CancellationToken.None);
            Assert.Equal(3UL, batchReader.Index.LineCount);
        }
    }

    [Fact]
    public void OpenAndSearchRejectDirectoryWithInvalidDataException()
    {
        var openEx = Assert.Throws<InvalidDataException>(() => LogReader.Open(_dir, null));
        Assert.Equal("Выберите обычный файл.", openEx.Message);

        var searchEx = Assert.Throws<InvalidDataException>(() =>
            LogSearch.SearchPage(
                _dir,
                null,
                new LogSearchQuery("a", false, true, false),
                0,
                10,
                LogSearchLimits.Default,
                CancellationToken.None));
        Assert.Equal("Выберите обычный файл.", searchEx.Message);

        Assert.Throws<FileNotFoundException>(() => LogReader.Open(Path.Combine(_dir, "missing.log"), null));
    }

    [Fact]
    public void SparseIndexHandlesVector512And256KChunkBoundaries()
    {
        foreach (int lane in new[] { 64, 256 })
        {
            var simdPath = Path.Combine(_dir, $"crlf-simd-{lane}.txt");
            var simdBytes = new byte[lane * 3 + 5];
            Array.Fill(simdBytes, (byte)'a');
            simdBytes[lane - 1] = (byte)'\r';
            simdBytes[lane] = (byte)'\n';
            simdBytes[lane * 2 - 1] = (byte)'\r';
            simdBytes[lane * 2] = (byte)'\n';
            File.WriteAllBytes(simdPath, simdBytes);

            using var singleReader = LogReader.Open(simdPath, null);
            singleReader.Index.Advance(singleReader.Handle, singleReader.Encoding, singleReader.Size, CancellationToken.None);
            Assert.Equal(3UL, singleReader.Index.LineCount);

            using var batchReader = LogReader.Open(simdPath, null);
            batchReader.Index.Advance(batchReader.Handle, batchReader.Encoding, (ulong)lane, CancellationToken.None);
            Assert.Equal(1UL, batchReader.Index.LineCount);
            batchReader.Index.Advance(batchReader.Handle, batchReader.Encoding, batchReader.Size, CancellationToken.None);
            Assert.Equal(3UL, batchReader.Index.LineCount);
        }

        const int chunk256K = 256 * 1024;
        var chunkPath = Path.Combine(_dir, "crlf-256k-anchors.txt");
        var chunkBytes = new byte[chunk256K + 4096];
        Array.Fill(chunkBytes, (byte)'x');
        chunkBytes[65535] = (byte)'\n';
        chunkBytes[131071] = (byte)'\n';
        chunkBytes[196607] = (byte)'\n';
        chunkBytes[chunk256K - 1] = (byte)'\r';
        chunkBytes[chunk256K] = (byte)'\n';
        chunkBytes[chunk256K + 100] = (byte)'\n';
        File.WriteAllBytes(chunkPath, chunkBytes);

        SparseLine[] expectedAnchors =
        [
            new(1, 0),
            new(2, 65536),
            new(3, 131072),
            new(4, 196608),
            new(5, (ulong)chunk256K + 1),
        ];

        using (var singleChunk = LogReader.Open(chunkPath, null))
        {
            singleChunk.Index.Advance(singleChunk.Handle, singleChunk.Encoding, singleChunk.Size, CancellationToken.None);
            Assert.Equal(6UL, singleChunk.Index.LineCount);
            Assert.Equal(expectedAnchors, singleChunk.Index.Entries);
            Assert.Equal((ulong)chunk256K + 1UL, singleChunk.Index.LineStartAt(singleChunk.Handle, singleChunk.Encoding, (ulong)chunk256K + 10, singleChunk.Size));
        }

        using (var incChunk = LogReader.Open(chunkPath, null))
        {
            incChunk.Index.Advance(incChunk.Handle, incChunk.Encoding, (ulong)chunk256K, CancellationToken.None);
            Assert.Equal(4UL, incChunk.Index.LineCount);
            incChunk.Index.Advance(incChunk.Handle, incChunk.Encoding, incChunk.Size, CancellationToken.None);
            Assert.Equal(6UL, incChunk.Index.LineCount);
            Assert.Equal(expectedAnchors, incChunk.Index.Entries);
        }

        var unrollBoundaryPath = Path.Combine(_dir, "crlf-256k-unroll.txt");
        var unrollBytes = new byte[chunk256K + 64];
        Array.Fill(unrollBytes, (byte)'x');
        unrollBytes[65535] = (byte)'\n';
        unrollBytes[131071] = (byte)'\n';
        unrollBytes[196607] = (byte)'\n';
        unrollBytes[chunk256K - 1024] = (byte)'\n';
        unrollBytes[chunk256K - 1] = (byte)'\r';
        unrollBytes[chunk256K] = (byte)'\n';
        File.WriteAllBytes(unrollBoundaryPath, unrollBytes);

        using (var singleUnroll = LogReader.Open(unrollBoundaryPath, null))
        {
            singleUnroll.Index.Advance(singleUnroll.Handle, singleUnroll.Encoding, singleUnroll.Size, CancellationToken.None);
            Assert.Equal(6UL, singleUnroll.Index.LineCount);
            Assert.Equal((ulong)chunk256K + 1UL, singleUnroll.Index.LineStartAt(singleUnroll.Handle, singleUnroll.Encoding, (ulong)chunk256K + 10, singleUnroll.Size));
        }

        using (var incUnroll = LogReader.Open(unrollBoundaryPath, null))
        {
            incUnroll.Index.Advance(incUnroll.Handle, incUnroll.Encoding, (ulong)chunk256K, CancellationToken.None);
            Assert.Equal(5UL, incUnroll.Index.LineCount);
            incUnroll.Index.Advance(incUnroll.Handle, incUnroll.Encoding, incUnroll.Size, CancellationToken.None);
            Assert.Equal(6UL, incUnroll.Index.LineCount);
        }

        var crPath = Path.Combine(_dir, "cr-256k-and-eof.txt");
        var crBytes = new byte[chunk256K + 16];
        Array.Fill(crBytes, (byte)'x');
        crBytes[chunk256K - 1] = (byte)'\r';
        crBytes[chunk256K] = (byte)'a';
        crBytes[chunk256K + 15] = (byte)'\r';
        File.WriteAllBytes(crPath, crBytes);

        using (var singleCr = LogReader.Open(crPath, null))
        {
            singleCr.Index.Advance(singleCr.Handle, singleCr.Encoding, singleCr.Size, CancellationToken.None);
            Assert.Equal(3UL, singleCr.Index.LineCount);
            Assert.Equal((ulong)chunk256K, singleCr.Index.LineStartAt(singleCr.Handle, singleCr.Encoding, (ulong)chunk256K + 5, singleCr.Size));
        }

        using (var incCr = LogReader.Open(crPath, null))
        {
            incCr.Index.Advance(incCr.Handle, incCr.Encoding, (ulong)chunk256K, CancellationToken.None);
            Assert.Equal(2UL, incCr.Index.LineCount);
            incCr.Index.Advance(incCr.Handle, incCr.Encoding, incCr.Size, CancellationToken.None);
            Assert.Equal(3UL, incCr.Index.LineCount);
        }
    }

    [Fact]
    public void SearchPageProgressiveBufferEscalationAndBoundaries()
    {
        var densePath = Path.Combine(_dir, "search-dense-16k.log");
        const string denseLine = "2026-11-21 INFO row=000 needle";
        byte[] denseRowBytes = Encoding.UTF8.GetBytes(denseLine + "\n");
        var denseBytes = new byte[50 * denseRowBytes.Length];
        for (int i = 0; i < 50; i++)
        {
            Buffer.BlockCopy(denseRowBytes, 0, denseBytes, i * denseRowBytes.Length, denseRowBytes.Length);
        }
        File.WriteAllBytes(densePath, denseBytes);

        var densePage = LogSearch.SearchPage(
            densePath,
            null,
            new LogSearchQuery("needle", false, true, false),
            0,
            32,
            LogSearchLimits.Default,
            CancellationToken.None);
        Assert.Equal(32, densePage.Hits.Count);
        Assert.False(densePage.Complete);
        Assert.False(densePage.WorkLimitReached);
        ulong expectedDenseEnd = 32UL * (ulong)denseRowBytes.Length;
        Assert.Equal(expectedDenseEnd, densePage.NextOffset);
        Assert.Equal(expectedDenseEnd, densePage.ScannedBytes);
        Assert.Equal((ulong)denseBytes.Length, densePage.FileSize);
        Assert.Equal(new LogSearchHit(0, (ulong)denseRowBytes.Length, denseLine), densePage.Hits[0]);
        Assert.Equal(new LogSearchHit(31UL * (ulong)denseRowBytes.Length, expectedDenseEnd, denseLine), densePage.Hits[31]);

        var crlf16KPath = Path.Combine(_dir, "search-crlf-16k.log");
        byte[] hit16K = "hit_16k\n"u8.ToArray();
        var buf16K = new byte[16 * 1024 + 1 + hit16K.Length];
        Array.Fill(buf16K, (byte)'x');
        buf16K[16 * 1024 - 1] = (byte)'\r';
        buf16K[16 * 1024] = (byte)'\n';
        Buffer.BlockCopy(hit16K, 0, buf16K, 16 * 1024 + 1, hit16K.Length);
        File.WriteAllBytes(crlf16KPath, buf16K);

        var page16K = LogSearch.SearchPage(
            crlf16KPath,
            null,
            new LogSearchQuery("hit_16k", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var single16K = Assert.Single(page16K.Hits);
        Assert.Equal(16UL * 1024UL + 1UL, single16K.LineStart);
        Assert.Equal((ulong)buf16K.Length, single16K.LineEnd);
        Assert.Equal("hit_16k", single16K.Preview);
        Assert.True(page16K.Complete);

        var crlf64KRefillPath = Path.Combine(_dir, "search-crlf-64k-refill.log");
        byte[] earlyHit = "hit_early\n"u8.ToArray();
        byte[] lateHit = "hit_64k\n"u8.ToArray();
        var buf64KRefill = new byte[64 * 1024 + 1 + lateHit.Length];
        Array.Fill(buf64KRefill, (byte)'x');
        Buffer.BlockCopy(earlyHit, 0, buf64KRefill, 0, earlyHit.Length);
        buf64KRefill[64 * 1024 - 1] = (byte)'\r';
        buf64KRefill[64 * 1024] = (byte)'\n';
        Buffer.BlockCopy(lateHit, 0, buf64KRefill, 64 * 1024 + 1, lateHit.Length);
        File.WriteAllBytes(crlf64KRefillPath, buf64KRefill);

        var page64KRefill = LogSearch.SearchPage(
            crlf64KRefillPath,
            null,
            new LogSearchQuery("hit_", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        Assert.Equal(2, page64KRefill.Hits.Count);
        Assert.Equal(new LogSearchHit(0, (ulong)earlyHit.Length, "hit_early"), page64KRefill.Hits[0]);
        Assert.Equal(new LogSearchHit(64UL * 1024UL + 1UL, (ulong)buf64KRefill.Length, "hit_64k"), page64KRefill.Hits[1]);
        Assert.True(page64KRefill.Complete);

        var crlf64KEscalatePath = Path.Combine(_dir, "search-crlf-64k-escalate.log");
        var buf64KEscalate = new byte[64 * 1024 + 1 + lateHit.Length];
        Array.Fill(buf64KEscalate, (byte)'x');
        buf64KEscalate[64 * 1024 - 1] = (byte)'\r';
        buf64KEscalate[64 * 1024] = (byte)'\n';
        Buffer.BlockCopy(lateHit, 0, buf64KEscalate, 64 * 1024 + 1, lateHit.Length);
        File.WriteAllBytes(crlf64KEscalatePath, buf64KEscalate);

        var page64KEscalate = LogSearch.SearchPage(
            crlf64KEscalatePath,
            null,
            new LogSearchQuery("hit_64k", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var single64K = Assert.Single(page64KEscalate.Hits);
        Assert.Equal(64UL * 1024UL + 1UL, single64K.LineStart);
        Assert.Equal((ulong)buf64KEscalate.Length, single64K.LineEnd);
        Assert.Equal("hit_64k", single64K.Preview);

        var longPath = Path.Combine(_dir, "search-long-96k.log");
        const int prefix96K = 96 * 1024;
        byte[] tailHit = "tail_hit\n"u8.ToArray();
        var longBytes = new byte[prefix96K + tailHit.Length];
        Array.Fill(longBytes, (byte)'a');
        Buffer.BlockCopy(tailHit, 0, longBytes, prefix96K, tailHit.Length);
        File.WriteAllBytes(longPath, longBytes);

        var longPage = LogSearch.SearchPage(
            longPath,
            null,
            new LogSearchQuery("tail_hit", false, true, false),
            0,
            10,
            new LogSearchLimits(LogIo.DefaultSearchBytes, 256 * 1024, 16),
            CancellationToken.None);
        var longHit = Assert.Single(longPage.Hits);
        Assert.Equal(0UL, longHit.LineStart);
        Assert.Equal((ulong)longBytes.Length, longHit.LineEnd);
        Assert.Equal(new string('a', 16), longHit.Preview);
        Assert.True(longPage.Complete);

        var limitEx = Assert.Throws<LogLineLimitException>(() =>
            LogSearch.SearchPage(
                longPath,
                null,
                new LogSearchQuery("tail_hit", false, true, false),
                0,
                10,
                new LogSearchLimits(LogIo.DefaultSearchBytes, 80 * 1024, 256),
                CancellationToken.None));
        Assert.Equal(0UL, limitEx.Offset);
        Assert.Equal(80 * 1024, limitEx.Limit);

        var bigPath = Path.Combine(_dir, "search-over-1mib.log");
        const int row1K = 1024;
        const int rows1536 = 1536;
        byte[] fillerRow = new byte[row1K];
        Array.Fill(fillerRow, (byte)'x');
        fillerRow[^1] = (byte)'\n';
        byte[] eofCyrillic = Encoding.UTF8.GetBytes("ошибка_в_конце");
        var bigBytes = new byte[rows1536 * row1K + eofCyrillic.Length];
        for (int i = 0; i < rows1536; i++)
        {
            Buffer.BlockCopy(fillerRow, 0, bigBytes, i * row1K, row1K);
        }
        Buffer.BlockCopy(eofCyrillic, 0, bigBytes, rows1536 * row1K, eofCyrillic.Length);
        File.WriteAllBytes(bigPath, bigBytes);

        var missPage = LogSearch.SearchPage(
            bigPath,
            null,
            new LogSearchQuery("отсутствует", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        Assert.Empty(missPage.Hits);
        Assert.True(missPage.Complete);
        Assert.Equal((ulong)bigBytes.Length, missPage.ScannedBytes);

        var eofHitPage = LogSearch.SearchPage(
            bigPath,
            null,
            new LogSearchQuery("ошибка_в_конце", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var eofHit = Assert.Single(eofHitPage.Hits);
        Assert.Equal((ulong)(rows1536 * row1K), eofHit.LineStart);
        Assert.Equal((ulong)bigBytes.Length, eofHit.LineEnd);
        Assert.Equal("ошибка_в_конце", eofHit.Preview);
        Assert.True(eofHitPage.Complete);
    }

    [Fact]
    public void SearchPageDirectUtf8LiteralAndFallbackEdgeCases()
    {
        var bomPath = Path.Combine(_dir, "search-utf8-bom.log");
        byte[] bomBytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("hello\nworld\n")];
        File.WriteAllBytes(bomPath, bomBytes);

        var bomHitPage = LogSearch.SearchPage(
            bomPath,
            null,
            new LogSearchQuery("hello", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var bomHit = Assert.Single(bomHitPage.Hits);
        Assert.Equal(0UL, bomHit.LineStart);
        Assert.Equal(9UL, bomHit.LineEnd);
        Assert.Equal("hello", bomHit.Preview);

        var strippedBomPage = LogSearch.SearchPage(
            bomPath,
            null,
            new LogSearchQuery("\uFEFF", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        Assert.Empty(strippedBomPage.Hits);

        var invalidUtf8Path = Path.Combine(_dir, "search-invalid-utf8.log");
        byte[] invalidBytes =
        [
            .. "ok\n"u8.ToArray(),
            .. "pre_"u8.ToArray(),
            0xC3, 0x28,
            .. Encoding.UTF8.GetBytes("_привет_"),
            0xFF, 0xFE,
            .. "_end\n"u8.ToArray(),
        ];
        File.WriteAllBytes(invalidUtf8Path, invalidBytes);

        var replacementPage = LogSearch.SearchPage(
            invalidUtf8Path,
            null,
            new LogSearchQuery("\uFFFD", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var replacementHit = Assert.Single(replacementPage.Hits);
        Assert.Equal(3UL, replacementHit.LineStart);
        Assert.Equal((ulong)invalidBytes.Length, replacementHit.LineEnd);
        Assert.Contains("привет", replacementHit.Preview);

        var adjacentCyrillicPage = LogSearch.SearchPage(
            invalidUtf8Path,
            null,
            new LogSearchQuery("привет", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var adjacentHit = Assert.Single(adjacentCyrillicPage.Hits);
        Assert.Equal(3UL, adjacentHit.LineStart);
        Assert.Equal((ulong)invalidBytes.Length, adjacentHit.LineEnd);

        var skipPath = Path.Combine(_dir, "search-midline-skip.log");
        File.WriteAllBytes(skipPath, "first_needle\nsecond_needle\n"u8.ToArray());

        var skipLiteralPage = LogSearch.SearchPage(
            skipPath,
            null,
            new LogSearchQuery("needle", false, true, false),
            3,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var skipLitHit = Assert.Single(skipLiteralPage.Hits);
        Assert.Equal(13UL, skipLitHit.LineStart);
        Assert.Equal(27UL, skipLitHit.LineEnd);
        Assert.Equal("second_needle", skipLitHit.Preview);

        var skipRegexPage = LogSearch.SearchPage(
            skipPath,
            null,
            new LogSearchQuery("needle$", true, true, false),
            3,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var skipRegexHit = Assert.Single(skipRegexPage.Hits);
        Assert.Equal(13UL, skipRegexHit.LineStart);
        Assert.Equal(27UL, skipRegexHit.LineEnd);
        Assert.Equal("second_needle", skipRegexHit.Preview);

        var partialUtf8Path = Path.Combine(_dir, "search-trailing-incomplete-utf8.log");
        File.WriteAllBytes(partialUtf8Path, [.. "line1\nhit_"u8.ToArray(), 0xF0, 0x9F, 0x99]);

        var partialHitPage = LogSearch.SearchPage(
            partialUtf8Path,
            null,
            new LogSearchQuery("hit", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var partialHit = Assert.Single(partialHitPage.Hits);
        Assert.Equal(6UL, partialHit.LineStart);
        Assert.Equal(10UL, partialHit.LineEnd);
        Assert.Equal("hit_", partialHit.Preview);
        Assert.Equal(6UL, partialHitPage.PendingLineStart);
        Assert.Equal(6UL, partialHitPage.NextOffset);
        Assert.False(partialHitPage.Complete);

        var partialMissPage = LogSearch.SearchPage(
            partialUtf8Path,
            null,
            new LogSearchQuery("miss", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        Assert.Empty(partialMissPage.Hits);
        Assert.Equal(6UL, partialMissPage.PendingLineStart);
        Assert.Equal(6UL, partialMissPage.NextOffset);
        Assert.False(partialMissPage.Complete);

        var utf16LePath = Path.Combine(_dir, "search-u16le.log");
        byte[] u16LeBytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("alpha\r\nПривет_мир\r\n")];
        File.WriteAllBytes(utf16LePath, u16LeBytes);
        var u16LePage = LogSearch.SearchPage(
            utf16LePath,
            null,
            new LogSearchQuery("Привет", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var u16LeHit = Assert.Single(u16LePage.Hits);
        Assert.Equal(16UL, u16LeHit.LineStart);
        Assert.Equal(40UL, u16LeHit.LineEnd);
        Assert.Equal("Привет_мир", u16LeHit.Preview);

        var utf16BePath = Path.Combine(_dir, "search-u16be.log");
        byte[] u16BeBytes = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("alpha\nПривет_мир\n")];
        File.WriteAllBytes(utf16BePath, u16BeBytes);
        var u16BePage = LogSearch.SearchPage(
            utf16BePath,
            null,
            new LogSearchQuery("Привет", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var u16BeHit = Assert.Single(u16BePage.Hits);
        Assert.Equal(14UL, u16BeHit.LineStart);
        Assert.Equal(36UL, u16BeHit.LineEnd);
        Assert.Equal("Привет_мир", u16BeHit.Preview);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var win1251 = Encoding.GetEncoding(1251);
        var win1251Path = Path.Combine(_dir, "search-win1251.log");
        File.WriteAllBytes(win1251Path, win1251.GetBytes("строка_один\nОшибка_1251\n"));
        var win1251Page = LogSearch.SearchPage(
            win1251Path,
            TextEncoding.Windows1251,
            new LogSearchQuery("Ошибка", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var win1251Hit = Assert.Single(win1251Page.Hits);
        Assert.Equal(12UL, win1251Hit.LineStart);
        Assert.Equal(24UL, win1251Hit.LineEnd);
        Assert.Equal("Ошибка_1251", win1251Hit.Preview);

        var wordAndCasePath = Path.Combine(_dir, "search-word-case.log");
        File.WriteAllBytes(wordAndCasePath, Encoding.UTF8.GetBytes("foobar\nfoo bar\nAlpha\nBETA\nПривет\n"));
        var wholeWordPage = LogSearch.SearchPage(
            wordAndCasePath,
            null,
            new LogSearchQuery("foo", false, true, true),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        var wholeWordHit = Assert.Single(wholeWordPage.Hits);
        Assert.Equal(7UL, wholeWordHit.LineStart);
        Assert.Equal("foo bar", wholeWordHit.Preview);

        var ignoreCaseAsciiPage = LogSearch.SearchPage(
            wordAndCasePath,
            null,
            new LogSearchQuery("beta", false, false, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        Assert.Equal("BETA", Assert.Single(ignoreCaseAsciiPage.Hits).Preview);

        var ignoreCaseCyrillicPage = LogSearch.SearchPage(
            wordAndCasePath,
            null,
            new LogSearchQuery("привет", false, false, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None);
        Assert.Equal("Привет", Assert.Single(ignoreCaseCyrillicPage.Hits).Preview);

        var emptyPath = Path.Combine(_dir, "search-empty.log");
        File.WriteAllBytes(emptyPath, []);
        Assert.Throws<ArgumentException>(() =>
            LogSearch.SearchPage(
                emptyPath,
                null,
                new LogSearchQuery("[unclosed", true, true, false),
                0,
                10,
                LogSearchLimits.Default,
                CancellationToken.None));

        var previewPath = Path.Combine(_dir, "search-preview-truncation.log");
        File.WriteAllBytes(previewPath, Encoding.UTF8.GetBytes("abcdefghij\nабвгд\na🙂🙂\n"));

        var asciiPreview = Assert.Single(LogSearch.SearchPage(
            previewPath,
            null,
            new LogSearchQuery("abcdefghij", false, true, false),
            0,
            10,
            new LogSearchLimits(LogIo.DefaultSearchBytes, LogIo.DefaultSearchLineBytes, 5),
            CancellationToken.None).Hits).Preview;
        Assert.Equal("abcde", asciiPreview);
        Assert.True(Encoding.UTF8.GetByteCount(asciiPreview) <= 5);

        var cyrillicPreview = Assert.Single(LogSearch.SearchPage(
            previewPath,
            null,
            new LogSearchQuery("абвгд", false, true, false),
            0,
            10,
            new LogSearchLimits(LogIo.DefaultSearchBytes, LogIo.DefaultSearchLineBytes, 5),
            CancellationToken.None).Hits).Preview;
        Assert.Equal("аб", cyrillicPreview);
        Assert.True(Encoding.UTF8.GetByteCount(cyrillicPreview) <= 5);

        var emojiTightPreview = Assert.Single(LogSearch.SearchPage(
            previewPath,
            null,
            new LogSearchQuery("a🙂", false, true, false),
            0,
            10,
            new LogSearchLimits(LogIo.DefaultSearchBytes, LogIo.DefaultSearchLineBytes, 4),
            CancellationToken.None).Hits).Preview;
        Assert.Equal("a", emojiTightPreview);
        Assert.True(Encoding.UTF8.GetByteCount(emojiTightPreview) <= 4);

        var emojiFitOnePreview = Assert.Single(LogSearch.SearchPage(
            previewPath,
            null,
            new LogSearchQuery("a🙂", false, true, false),
            0,
            10,
            new LogSearchLimits(LogIo.DefaultSearchBytes, LogIo.DefaultSearchLineBytes, 8),
            CancellationToken.None).Hits).Preview;
        Assert.Equal("a🙂", emojiFitOnePreview);
        Assert.True(Encoding.UTF8.GetByteCount(emojiFitOnePreview) <= 8);
        Assert.False(char.IsHighSurrogate(emojiFitOnePreview[^1]));
    }

    [Fact]
    public void SearchPagePrefilterSkipHandlesChunkStraddlingMatchesAndFullMiss()
    {
        var p = Path.Combine(_dir, "search-chunk-straddle.log");
        var fillerLine = Encoding.UTF8.GetBytes("2026-11-21T00:00:00Z INFO worker=alpha payload=regular_line_content\r\n");
        var hit16kLine = Encoding.UTF8.GetBytes("prefix_before_boundary_STRADDLE_16K_MATCH_suffix\r\n");
        var hit480kLine = Encoding.UTF8.GetBytes("prefix_before_boundary_STRADDLE_480K_MATCH_suffix\r\n");

        static void PadToTarget(MemoryStream stream, byte[] filler, long target)
        {
            while (stream.Length + filler.Length <= target - 2)
            {
                stream.Write(filler);
            }
            long rem = target - stream.Length;
            if (rem >= 2)
            {
                byte[] pad = new byte[rem];
                Array.Fill(pad, (byte)'p', 0, (int)rem - 2);
                pad[^2] = (byte)'\r';
                pad[^1] = (byte)'\n';
                stream.Write(pad);
            }
        }

        using var ms = new MemoryStream(2 * 1024 * 1024);
        const long boundary16k = 16 * 1024;
        const long boundary480k = 16 * 1024 + 480 * 1024;
        PadToTarget(ms, fillerLine, boundary16k - 30);
        ulong hit16kOffset = (ulong)ms.Length;
        ms.Write(hit16kLine);
        Assert.True((long)hit16kOffset < boundary16k && (long)hit16kOffset + hit16kLine.Length > boundary16k);

        PadToTarget(ms, fillerLine, boundary480k - 30);
        ulong hit480kOffset = (ulong)ms.Length;
        ms.Write(hit480kLine);
        Assert.True((long)hit480kOffset < boundary480k && (long)hit480kOffset + hit480kLine.Length > boundary480k);

        while (ms.Length < 1_600_000)
        {
            ms.Write(fillerLine);
        }

        byte[] data = ms.ToArray();
        File.WriteAllBytes(p, data);

        var hit16k = LogSearch.SearchPage(
            p,
            null,
            new LogSearchQuery("STRADDLE_16K_MATCH", false, true, false),
            0,
            10,
            new LogSearchLimits((ulong)data.Length, LogIo.DefaultSearchLineBytes, LogIo.DefaultRowBytes),
            CancellationToken.None);
        Assert.Single(hit16k.Hits);
        Assert.Equal(hit16kOffset, hit16k.Hits[0].LineStart);
        Assert.True(hit16k.Complete);
        Assert.Equal((ulong)data.Length, hit16k.NextOffset);

        var hit480k = LogSearch.SearchPage(
            p,
            null,
            new LogSearchQuery("STRADDLE_480K_MATCH", false, true, false),
            0,
            10,
            new LogSearchLimits((ulong)data.Length, LogIo.DefaultSearchLineBytes, LogIo.DefaultRowBytes),
            CancellationToken.None);
        Assert.Single(hit480k.Hits);
        Assert.Equal(hit480kOffset, hit480k.Hits[0].LineStart);
        Assert.True(hit480k.Complete);
        Assert.Equal((ulong)data.Length, hit480k.NextOffset);

        var miss = LogSearch.SearchPage(
            p,
            null,
            new LogSearchQuery("NEVER_PRESENT_TOKEN", false, true, false),
            0,
            32,
            new LogSearchLimits((ulong)data.Length, LogIo.DefaultSearchLineBytes, LogIo.DefaultRowBytes),
            CancellationToken.None);
        Assert.Empty(miss.Hits);
        Assert.True(miss.Complete);
        Assert.False(miss.WorkLimitReached);
        Assert.Equal((ulong)data.Length, miss.NextOffset);
    }

    [Fact]
    public void SearchPageAcceptsExactMaxLineBytesAndPrefilterSkipsReplacementChar()
    {
        const int exactLimit = 16 * 1024;
        var exactPath = Path.Combine(_dir, "exact-16k-line.log");
        var exactBytes = new byte[exactLimit + 1];
        Array.Fill(exactBytes, (byte)'a', 0, exactLimit);
        exactBytes[exactLimit] = (byte)'\n';
        File.WriteAllBytes(exactPath, exactBytes);

        var exactPage = LogSearch.SearchPage(
            exactPath,
            null,
            new LogSearchQuery("aaaa", false, true, false),
            0,
            10,
            new LogSearchLimits(LogIo.DefaultSearchBytes, exactLimit, LogIo.DefaultRowBytes),
            CancellationToken.None);
        Assert.Single(exactPage.Hits);
        Assert.Equal(0UL, exactPage.Hits[0].LineStart);
        Assert.Equal((ulong)(exactLimit + 1), exactPage.Hits[0].LineEnd);
        Assert.True(exactPage.Complete);

        var overPath = Path.Combine(_dir, "over-16k-line.log");
        var overBytes = new byte[exactLimit + 2];
        Array.Fill(overBytes, (byte)'a', 0, exactLimit + 1);
        overBytes[exactLimit + 1] = (byte)'\n';
        File.WriteAllBytes(overPath, overBytes);

        Assert.Throws<LogLineLimitException>(() => LogSearch.SearchPage(
            overPath,
            null,
            new LogSearchQuery("aaaa", false, true, false),
            0,
            10,
            new LogSearchLimits(LogIo.DefaultSearchBytes, exactLimit, LogIo.DefaultRowBytes),
            CancellationToken.None));

        var invalidPath = Path.Combine(_dir, "invalid-prefilter-fffd.log");
        using var ms = new MemoryStream(4096);
        var padLine = Encoding.UTF8.GetBytes("regular filler line without invalid bytes\n");
        while (ms.Length < 2048)
        {
            ms.Write(padLine);
        }
        ulong targetOffset = (ulong)ms.Length;
        ms.Write(Encoding.UTF8.GetBytes("left"));
        ms.WriteByte(0xFF);
        ms.Write(Encoding.UTF8.GetBytes("right\n"));
        ms.Write(padLine);
        File.WriteAllBytes(invalidPath, ms.ToArray());

        var wholeWordHit = Assert.Single(LogSearch.SearchPage(
            invalidPath,
            null,
            new LogSearchQuery("left\uFFFDright", false, true, true),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None).Hits);
        Assert.Equal(targetOffset, wholeWordHit.LineStart);

        var chunkFffdHit = Assert.Single(LogSearch.SearchPage(
            invalidPath,
            null,
            new LogSearchQuery("\uFFFD", false, true, false),
            0,
            10,
            LogSearchLimits.Default,
            CancellationToken.None).Hits);
        Assert.Equal(targetOffset, chunkFffdHit.LineStart);
    }

    [Fact]
    public void SparseIndexHandles512KInternalChunkCrlfSplit()
    {
        const int chunk512K = 512 * 1024;
        var crlfPath = Path.Combine(_dir, "crlf-512k-internal.txt");
        var crlfBytes = new byte[chunk512K + 128];
        Array.Fill(crlfBytes, (byte)'x');
        crlfBytes[chunk512K - 1] = (byte)'\r';
        crlfBytes[chunk512K] = (byte)'\n';
        crlfBytes[chunk512K + 64] = (byte)'\n';
        File.WriteAllBytes(crlfPath, crlfBytes);

        using (var r = LogReader.Open(crlfPath, null))
        {
            r.Index.Advance(r.Handle, r.Encoding, r.Size, CancellationToken.None);
            Assert.Equal(3UL, r.Index.LineCount);
            Assert.Equal((ulong)chunk512K + 1UL, r.Index.LineStartAt(r.Handle, r.Encoding, (ulong)chunk512K + 10, r.Size));
        }

        var crPath = Path.Combine(_dir, "cr-512k-internal.txt");
        var crBytes = new byte[chunk512K + 16];
        Array.Fill(crBytes, (byte)'x');
        crBytes[chunk512K - 1] = (byte)'\r';
        crBytes[chunk512K] = (byte)'a';
        File.WriteAllBytes(crPath, crBytes);

        using (var r2 = LogReader.Open(crPath, null))
        {
            r2.Index.Advance(r2.Handle, r2.Encoding, r2.Size, CancellationToken.None);
            Assert.Equal(2UL, r2.Index.LineCount);
            Assert.Equal((ulong)chunk512K, r2.Index.LineStartAt(r2.Handle, r2.Encoding, (ulong)chunk512K + 5, r2.Size));
        }
    }

    [Fact]
    public void SparseIndexParallelScanMatchesSequentialAndHandlesSliceBoundaryCrlf()
    {
        const int totalSize = 10 * 1024 * 1024 + 512;
        var data = new byte[totalSize];
        Array.Fill(data, (byte)'x');

        int[] seamOffsets =
        [
            64 * 1024,
            1024 * 1024,
            2 * 1024 * 1024,
            4 * 1024 * 1024,
            5 * 1024 * 1024,
            8 * 1024 * 1024,
        ];

        for (int off = 254; off + 2 < totalSize; off += 256)
        {
            bool nearSeam = false;
            for (int s = 0; s < seamOffsets.Length; s++)
            {
                if (Math.Abs(off - seamOffsets[s]) <= 16)
                {
                    nearSeam = true;
                    break;
                }
            }

            if (!nearSeam)
            {
                data[off] = (byte)'\r';
                data[off + 1] = (byte)'\n';
            }
        }

        data[64 * 1024 - 1] = (byte)'\r';
        data[64 * 1024] = (byte)'\n';

        data[1024 * 1024 - 2] = (byte)'\r';
        data[1024 * 1024 - 1] = (byte)'\r';
        data[1024 * 1024] = (byte)'\n';

        data[2 * 1024 * 1024 - 1] = (byte)'\r';
        data[2 * 1024 * 1024] = (byte)'a';

        data[4 * 1024 * 1024 - 1] = (byte)'\r';
        data[4 * 1024 * 1024] = (byte)'\n';

        data[5 * 1024 * 1024 - 2] = (byte)'\r';
        data[5 * 1024 * 1024 - 1] = (byte)'\r';
        data[5 * 1024 * 1024] = (byte)'\n';

        data[8 * 1024 * 1024 - 1] = (byte)'\r';
        data[8 * 1024 * 1024] = (byte)'\n';

        var expectedLineStarts = new List<ulong> { 0 };
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == (byte)'\n' || (data[i] == (byte)'\r' && (i + 1 >= data.Length || data[i + 1] != (byte)'\n')))
            {
                expectedLineStarts.Add((ulong)(i + 1));
            }
        }

        ulong expectedTotalLines = (ulong)expectedLineStarts.Count;
        var path = Path.Combine(_dir, "parallel-crlf-seams.log");
        File.WriteAllBytes(path, data);

        using var parallelReader = LogReader.Open(path, null);
        ulong parallelAdvanced = parallelReader.Index.Advance(parallelReader.Handle, parallelReader.Encoding, parallelReader.Size, CancellationToken.None);
        Assert.Equal(parallelReader.Size, parallelAdvanced);
        Assert.Equal(parallelReader.Size, parallelReader.Index.ScannedTo);
        Assert.Equal(expectedTotalLines, parallelReader.Index.LineCount);
        if (OperatingSystem.IsWindows() && Environment.ProcessorCount >= 2)
        {
            Assert.True(parallelReader.Index.LastAdvanceUsedParallel);
        }

        using var sequentialReader = LogReader.Open(path, null);
        const ulong seqChunk = 2UL * 1024 * 1024;
        ulong target = seqChunk;
        while (sequentialReader.Index.ScannedTo < sequentialReader.Size)
        {
            sequentialReader.Index.Advance(sequentialReader.Handle, sequentialReader.Encoding, Math.Min(target, sequentialReader.Size), CancellationToken.None);
            Assert.False(sequentialReader.Index.LastAdvanceUsedParallel);
            target += seqChunk;
        }
        Assert.Equal(sequentialReader.Index.ScannedTo, parallelReader.Index.ScannedTo);
        Assert.Equal(sequentialReader.Index.LineCount, parallelReader.Index.LineCount);

        var entries = parallelReader.Index.Entries;
        Assert.True(entries.Count > 1);
        Assert.Equal(new SparseLine(1, 0), entries[0]);
        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(entries[i].Line > entries[i - 1].Line);
            Assert.True(entries[i].Offset > entries[i - 1].Offset);
            Assert.True(entries[i].Offset - entries[i - 1].Offset >= LineIndex.IndexSampleBytes);
        }

        ulong[] probeOffsets =
        [
            0UL,
            64UL * 1024 - 1,
            64UL * 1024 + 1,
            1024UL * 1024 - 1,
            1024UL * 1024 + 1,
            2UL * 1024 * 1024,
            2UL * 1024 * 1024 + 100,
            4UL * 1024 * 1024 - 1,
            4UL * 1024 * 1024 + 1,
            5UL * 1024 * 1024 - 1,
            5UL * 1024 * 1024 + 1,
            8UL * 1024 * 1024 - 1,
            8UL * 1024 * 1024 + 1,
            (ulong)totalSize - 10,
        ];

        foreach (ulong probe in probeOffsets)
        {
            int idx = expectedLineStarts.BinarySearch(probe);
            if (idx < 0)
            {
                idx = ~idx - 1;
            }
            var expectedLine = new SparseLine((ulong)idx + 1, expectedLineStarts[idx]);
            var anchor = parallelReader.Index.FindAnchor(probe);
            var actualLine = LineIndex.LineAtFromAnchor(parallelReader.Handle, parallelReader.Encoding, probe, parallelReader.Size, anchor);
            Assert.Equal(expectedLine, actualLine);
        }

        const ulong split8M = 8UL * 1024 * 1024;
        ulong expectedLinesBefore8M = (ulong)expectedLineStarts.Count(off => off <= split8M);

        using var partialReader = LogReader.Open(path, null);
        partialReader.Index.Advance(partialReader.Handle, partialReader.Encoding, split8M, CancellationToken.None);
        Assert.Equal(split8M, partialReader.Index.ScannedTo);
        Assert.Equal(expectedLinesBefore8M, partialReader.Index.LineCount);
        if (OperatingSystem.IsWindows() && Environment.ProcessorCount >= 2)
        {
            Assert.True(partialReader.Index.LastAdvanceUsedParallel);
        }

        partialReader.Index.Advance(partialReader.Handle, partialReader.Encoding, partialReader.Size, CancellationToken.None);
        Assert.False(partialReader.Index.LastAdvanceUsedParallel);
        Assert.Equal(partialReader.Size, partialReader.Index.ScannedTo);
        Assert.Equal(expectedTotalLines, partialReader.Index.LineCount);
    }
}
