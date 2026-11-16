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
}
