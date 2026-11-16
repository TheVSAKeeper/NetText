using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NetText.Core;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string tmp = Path.Combine(Path.GetTempPath(), "nt_corebench_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
try
{
    string log512 = Path.Combine(tmp, "log512.txt");
    byte[] lineBytes = Encoding.UTF8.GetBytes("2026-11-16T21:30:00Z INFO rusttext::bench line=12345 message=hello_world_test_data\r\n");
    byte[] block = new byte[4 * 1024 * 1024];
    int pos = 0;
    while (pos + lineBytes.Length <= block.Length)
    {
        Buffer.BlockCopy(lineBytes, 0, block, pos, lineBytes.Length);
        pos += lineBytes.Length;
    }
    using (var fs = new FileStream(log512, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1024 * 1024))
    {
        for (int i = 0; i < 128; i++) // ~512 MiB
        {
            fs.Write(block, 0, pos);
        }
        fs.Flush(true);
    }

    // Warmup OS cache on log512
    using (var reader = LogReader.Open(log512, null))
    {
        _ = reader.Index.Advance(reader.Handle, reader.Encoding, Math.Min(reader.Size, 16UL * 1024 * 1024), CancellationToken.None);
    }

    // 1. LineIndex.Advance on 512 MiB
    GC.Collect();
    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    ulong linesCount;
    ulong fileSize;
    using (var reader = LogReader.Open(log512, null))
    {
        fileSize = reader.Size;
        reader.Index.Advance(reader.Handle, reader.Encoding, fileSize, CancellationToken.None);
        linesCount = reader.Index.LineCount;
    }
    sw.Stop();
    long indexAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    double indexGbSec = (fileSize / (1024.0 * 1024.0 * 1024.0)) / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"LineIndex(512MiB): {sw.Elapsed.TotalMilliseconds:F1} ms ({indexGbSec:F2} GiB/s), lines={linesCount}, alloc={indexAlloc / 1048576.0:F2} MiB");

    // 2. LogSearch literal/regex miss over bounded slice (scale via MiB/s)
    var litQuery = new LogSearchQuery("NONEXISTENT_NEEDLE_XYZ", Regex: false, CaseSensitive: true, WholeWord: false);
    var regexQuery = new LogSearchQuery(@"line=\d+.*NONEXISTENT", Regex: true, CaseSensitive: true, WholeWord: false);
    // Warmup search
    _ = LogSearch.SearchPage(log512, null, litQuery, 0, 32, new LogSearchLimits(1024 * 1024, LogIo.DefaultSearchLineBytes, 300), CancellationToken.None);

    ulong searchScanBytes = fileSize;
    GC.Collect();
    allocBefore = GC.GetAllocatedBytesForCurrentThread();
    sw.Restart();
    var page1 = LogSearch.SearchPage(log512, null, litQuery, 0, 32, new LogSearchLimits(searchScanBytes, LogIo.DefaultSearchLineBytes, 300), CancellationToken.None);
    sw.Stop();
    long litAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    double litMbSec = (page1.ScannedBytes / 1048576.0) / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"LogSearchLiteralMiss({page1.ScannedBytes / 1048576.0:F0}MiB): {sw.Elapsed.TotalMilliseconds:F1} ms ({litMbSec:F1} MiB/s), alloc={litAlloc / 1048576.0:F2} MiB");

    GC.Collect();
    allocBefore = GC.GetAllocatedBytesForCurrentThread();
    sw.Restart();
    var page2 = LogSearch.SearchPage(log512, null, regexQuery, 0, 32, new LogSearchLimits(searchScanBytes, LogIo.DefaultSearchLineBytes, 300), CancellationToken.None);
    sw.Stop();
    long regAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    double regMbSec = (page2.ScannedBytes / 1048576.0) / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"LogSearchRegexMiss({page2.ScannedBytes / 1048576.0:F0}MiB): {sw.Elapsed.TotalMilliseconds:F1} ms ({regMbSec:F1} MiB/s), alloc={regAlloc / 1048576.0:F2} MiB");

    // 3. EditorSearch on 40 MiB string
    var sbJson = new StringBuilder(42 * 1024 * 1024);
    sbJson.Append("[\n");
    for (int i = 0; i < 450_000; i++)
    {
        sbJson.Append("  {\"id\": 12345, \"level\": \"INFO\", \"msg\": \"hello_world_test_data\", \"ok\": true},\n");
    }
    sbJson.Append("  {\"id\": 0}\n]");
    string json40 = sbJson.ToString();
    var sources = new[] { new SearchSource(1, json40) };
    var esQuery = new SearchQuery("hello_world_test_data", Regex: false, CaseSensitive: false, WholeWord: false, AllTabs: false);
    EditorSearch.TryRun(esQuery, SearchCommand.Next, sources, new SearchHit(1, new ByteRange(0, 0)), false, "", out _, out _);

    GC.Collect();
    allocBefore = GC.GetAllocatedBytesForCurrentThread();
    sw.Restart();
    EditorSearch.TryRun(esQuery, SearchCommand.Next, sources, new SearchHit(1, new ByteRange(0, 0)), false, "", out var esOutcome, out _);
    sw.Stop();
    long esAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    Console.WriteLine($"EditorSearch(40MiB): {sw.Elapsed.TotalMilliseconds:F1} ms, alloc={esAlloc / 1048576.0:F2} MiB, hits={(esOutcome as SearchOutcome.Found)?.Total}");

    // 4. JsonFormat on 40 MiB string & UTF-8 bytes
    JsonFormat.TryFormat("{\"a\":1}", "\n", out _, out _);
    GC.Collect();
    allocBefore = GC.GetAllocatedBytesForCurrentThread();
    sw.Restart();
    bool okJson = JsonFormat.TryFormat(json40, "\n", out string formattedJson, out _);
    sw.Stop();
    long jfAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    Console.WriteLine($"JsonFormat(40MiB): {sw.Elapsed.TotalMilliseconds:F1} ms, ok={okJson}, outLen={formattedJson.Length}, alloc={jfAlloc / 1048576.0:F2} MiB");

    byte[] json40Utf8 = Encoding.UTF8.GetBytes(json40);
    JsonFormat.TryFormatUtf8("{}"u8, "\n"u8, out _, out _);
    GC.Collect();
    allocBefore = GC.GetAllocatedBytesForCurrentThread();
    sw.Restart();
    bool okJsonUtf8 = JsonFormat.TryFormatUtf8(json40Utf8, "\n"u8, out byte[] formattedUtf8, out _);
    sw.Stop();
    long jfUtf8Alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    Console.WriteLine($"JsonFormatUtf8(40MiB): {sw.Elapsed.TotalMilliseconds:F1} ms, ok={okJsonUtf8}, outLen={formattedUtf8.Length}, alloc={jfUtf8Alloc / 1048576.0:F2} MiB");

    unsafe
    {
        if (JsonFormat.TryFormatUtf8Native("{}"u8, "\n"u8, out byte* wPtr, out _, out _))
        {
            NativeMemory.Free(wPtr);
        }
        GC.Collect();
        allocBefore = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        bool okNative = JsonFormat.TryFormatUtf8Native(json40Utf8, "\n"u8, out byte* nPtr, out int nLen, out _);
        if (okNative) NativeMemory.Free(nPtr);
        sw.Stop();
        long jfNativeAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        Console.WriteLine($"JsonFormatUtf8Native(40MiB): {sw.Elapsed.TotalMilliseconds:F1} ms, ok={okNative}, outLen={nLen}, alloc={jfNativeAlloc / 1048576.0:F2} MiB");
    }

    // 5. Log random jump (50% & EOF) + page scroll
    using (var jumpReader = LogReader.Open(log512, null))
    {
        jumpReader.Index.Advance(jumpReader.Handle, jumpReader.Encoding, fileSize, CancellationToken.None);
        var opts = new PageOptions(60, 2048);
        _ = jumpReader.PageAtLine(fileSize / 2, opts);
        sw.Restart();
        var midPage = jumpReader.PageAtLine(fileSize / 2, opts);
        var endPage = jumpReader.PageBefore(fileSize, opts);
        var nextPage = jumpReader.RenderPage(midPage.NextOffset, opts);
        sw.Stop();
        Console.WriteLine($"LogJumpAndScroll(512MiB): {sw.Elapsed.TotalMilliseconds:F2} ms (50%={midPage.Rows.Count} rows, EOF={endPage.Rows.Count} rows, next={nextPage.Rows.Count} rows)");
    }

    // 6. Log search cancellation latency (<1ms)
    using (var cts = new CancellationTokenSource())
    {
        var searchTask = Task.Run(() =>
        {
            try
            {
                LogSearch.SearchPage(log512, null, regexQuery, 0, 32, new LogSearchLimits(fileSize, LogIo.DefaultSearchLineBytes, 300), cts.Token);
            }
            catch (LogCancelledException) { }
            catch (OperationCanceledException) { }
        });
        Thread.Sleep(25);
        long cancelStart = Stopwatch.GetTimestamp();
        cts.Cancel();
        searchTask.Wait(2000);
        double cancelMs = Stopwatch.GetElapsedTime(cancelStart).TotalMilliseconds;
        Console.WriteLine($"LogSearchCancel: {cancelMs:F2} ms");
    }

    // 7. Editor ReplaceAll on 40 MiB (string & UTF-8 pooled buffer)
    var repQuery = new SearchQuery("hello_world_test_data", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false);
    sw.Restart();
    EditorSearch.TryRun(repQuery, SearchCommand.ReplaceAll, sources, new SearchHit(1, new ByteRange(0, 0)), false, "replaced_ok", out var repOutcome, out _);
    sw.Stop();
    Console.WriteLine($"EditorReplaceAll(40MiB): {sw.Elapsed.TotalMilliseconds:F1} ms, count={(repOutcome as SearchOutcome.Replaced)?.Count}");

    var utf8Sources = new[] { new SearchUtf8Source(1, json40Utf8, json40Utf8.Length) };
    var esLitQuery = new SearchQuery("hello_world_test_data", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false);
    EditorSearch.TryRunUtf8(esLitQuery, SearchCommand.Next, utf8Sources, new SearchHit(1, new ByteRange(0, 0)), false, "", out _, out _);
    GC.Collect();
    allocBefore = GC.GetAllocatedBytesForCurrentThread();
    sw.Restart();
    EditorSearch.TryRunUtf8(esLitQuery, SearchCommand.Next, utf8Sources, new SearchHit(1, new ByteRange(0, 0)), false, "", out var esUtf8Outcome, out _);
    sw.Stop();
    long esUtf8Alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    Console.WriteLine($"EditorSearchUtf8(40MiB): {sw.Elapsed.TotalMilliseconds:F1} ms, alloc={esUtf8Alloc / 1048576.0:F2} MiB, hits={(esUtf8Outcome as SearchOutcome.Found)?.Total}");

    EditorSearch.TryRunUtf8(repQuery, SearchCommand.ReplaceAll, utf8Sources, new SearchHit(1, new ByteRange(0, 0)), false, "replaced_ok", out var repWarmup, out _);
    if (repWarmup is SearchOutcome.ReplacedUtf8 { FromPool: true, Buffer: var warmBuf } && warmBuf.Length > 0)
    {
        ArrayPool<byte>.Shared.Return(warmBuf);
    }
    GC.Collect();
    allocBefore = GC.GetAllocatedBytesForCurrentThread();
    sw.Restart();
    EditorSearch.TryRunUtf8(repQuery, SearchCommand.ReplaceAll, utf8Sources, new SearchHit(1, new ByteRange(0, 0)), false, "replaced_ok", out var repUtf8Outcome, out _);
    sw.Stop();
    long repUtf8Alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    int? repUtf8Count = (repUtf8Outcome as SearchOutcome.ReplacedUtf8)?.Count;
    if (repUtf8Outcome is SearchOutcome.ReplacedUtf8 { FromPool: true, Buffer: var repBuf } && repBuf.Length > 0)
    {
        ArrayPool<byte>.Shared.Return(repBuf);
    }
    Console.WriteLine($"EditorReplaceAllUtf8(40MiB): {sw.Elapsed.TotalMilliseconds:F1} ms, alloc={repUtf8Alloc / 1048576.0:F2} MiB, count={repUtf8Count}");
}
finally
{
    try { Directory.Delete(tmp, true); } catch { }
}
