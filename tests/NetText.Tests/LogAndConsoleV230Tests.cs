using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using NetText.Core;
using NetText.Native;
using Xunit;

namespace NetText.Tests;

public sealed class LogAndConsoleV230Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nettext-v230-{Guid.NewGuid():N}");

    public LogAndConsoleV230Tests()
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
    public void LineNumbersFormatsWithDotGroupSeparators()
    {
        Assert.Equal("0", LineNumbers.Format(0));
        Assert.Equal("999", LineNumbers.Format(999));
        Assert.Equal("1.000", LineNumbers.Format(1000));
        Assert.Equal("1.123.123", LineNumbers.Format(1_123_123));
        Assert.Equal("1.234.567.890", LineNumbers.Format(1_234_567_890L));
        Assert.Equal("-12.345", LineNumbers.Format(-12_345));
    }

    [Fact]
    public void LogBookmarkExtendsOptionalFieldsAndRoundTrips()
    {
        var bookmark = new LogBookmark
        {
            Offset = 128,
            Hidden = 64,
            AutoRefresh = false,
            FollowEnd = true,
            IndexOptIn = true,
            EditPages = true,
        };

        var copy = bookmark.Copy();
        Assert.Equal(bookmark.Offset, copy.Offset);
        Assert.Equal(bookmark.AutoRefresh, copy.AutoRefresh);
        Assert.Equal(true, copy.IndexOptIn);
        Assert.True(copy.EditPages);

        var tab = new SessionTab(
            1,
            new SessionFileTarget("app.log", null),
            new SessionFormat("UTF-8", "LF", false),
            new SessionSelection(0, 0),
            false,
            [0, 0],
            null,
            bookmark);
        var state = new SessionState([tab], 0, new SessionWindow(800, 600, false), false, 12f);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(state, SessionJsonContext.Default.SessionState);
        var restored = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionState)!;

        Assert.NotNull(restored.Tabs[0].Log);
        Assert.False(restored.Tabs[0].Log!.AutoRefresh);
        Assert.True(restored.Tabs[0].Log!.IndexOptIn);
        Assert.True(restored.Tabs[0].Log!.EditPages);
    }

    [Fact]
    public void LogLevelsClassifiesSyslogStructuredJsonHeadersAndUnknown()
    {
        Assert.Equal(LogLevel.Fatal, LogLevels.Classify("<0>kernel panic"));
        Assert.Equal(LogLevel.Error, LogLevels.Classify("<11>auth failure"));
        Assert.Equal(LogLevel.Warning, LogLevels.Classify("<12>disk almost full"));
        Assert.Equal(LogLevel.Info, LogLevels.Classify("<14>service started"));
        Assert.Equal(LogLevel.Debug, LogLevels.Classify("<191>trace detail"));
        Assert.Equal(LogLevel.Other, LogLevels.Classify("<192>out of syslog range"));

        Assert.Equal(LogLevel.Error, LogLevels.Classify("""{"ts":"2026-01-01","level":"error","msg":"boom"}"""));
        Assert.Equal(LogLevel.Warning, LogLevels.Classify("""{"@l":"WRN","@mt":"slow"}"""));
        Assert.Equal(LogLevel.Fatal, LogLevels.Classify("ts=12:00:00 severity=FATAL host=srv1"));
        Assert.Equal(LogLevel.Trace, LogLevels.Classify("log_level: trace body"));
        Assert.Equal(LogLevel.Debug, LogLevels.Classify("""{"severityText":"debug","msg":"x"}"""));
        Assert.Equal(LogLevel.Info, LogLevels.Classify("log.level=notice ready"));
        Assert.Equal(LogLevel.Other, LogLevels.Classify("""{"msg":"error happened in payload without level field"}"""));

        Assert.Equal(LogLevel.Error, LogLevels.Classify("[ERR] failed to open socket"));
        Assert.Equal(LogLevel.Info, LogLevels.Classify("2026-01-01 12:00:00.123 [INF] worker ready"));
        Assert.Equal(LogLevel.Warning, LogLevels.Classify("2026-01-01T12:00:00Z WARN retrying"));
        Assert.Equal(LogLevel.Fatal, LogLevels.Classify("| CRITICAL | shutdown"));
        Assert.Equal(LogLevel.Debug, LogLevels.Classify("[12:00:00 DBG] step"));

        Assert.Equal(LogLevel.Other, LogLevels.Classify("   at MyApp.Program.Main() in Program.cs:line 42"));
        Assert.Equal(LogLevel.Other, LogLevels.Classify(""));
    }

    [Fact]
    public void HasLogRecordsDetectsLogsAndRejectsProseAndBinary()
    {
        Assert.True(LogLevels.IsLogPath("server.LOG"));
        Assert.False(LogLevels.IsLogPath("notes.txt"));

        string explicitPrefix = Path.Combine(_dir, "explicit.txt");
        File.WriteAllText(explicitPrefix, "[ERR] failed on startup\nplain continuation\n");
        Assert.True(LogLevels.HasLogRecords(explicitPrefix));

        string datedTxt = Path.Combine(_dir, "dated.txt");
        File.WriteAllText(datedTxt,
            "2026-11-20 10:00:00 started\n" +
            "2026-11-20 10:00:01 running\n" +
            "some detail\n");
        Assert.True(LogLevels.HasLogRecords(datedTxt));

        string proseTxt = Path.Combine(_dir, "prose.txt");
        File.WriteAllText(proseTxt,
            "Hello team,\n" +
            "Please review the error handling guidelines.\n" +
            "There is no error in the budget.\n" +
            "Best regards.\n");
        Assert.False(LogLevels.HasLogRecords(proseTxt));

        string binaryTxt = Path.Combine(_dir, "binary.txt");
        File.WriteAllBytes(binaryTxt, Encoding.UTF8.GetBytes("[ERR] header\0binary\n"));
        Assert.False(LogLevels.HasLogRecords(binaryTxt));
    }

    [Fact]
    public void LogLevelFilterPagesLargeFileInheritsContinuationAndDetectsChanges()
    {
        string path = Path.Combine(_dir, "filter.log");
        var sb = new StringBuilder();
        for (int i = 0; i < 150; i++)
        {
            sb.Append($"2026-11-20 12:00:{i % 60:D2} [INF] noise line {i}\n");
            sb.Append($"2026-11-20 12:00:{i % 60:D2} [ERR] failure #{i}\n");
        }
        sb.Append("2026-11-20 12:05:00 [WRN] ");
        sb.Append('w', LogIo.DefaultRowBytes + 512);
        sb.Append('\n');
        File.WriteAllText(path, sb.ToString());

        var (obs, _) = LogFile.Observe(path, null);
        var p1 = LogLevelFilter.ReadFile(path, new FilterCursor(0), LogLevel.Error | LogLevel.Warning, obs.Stamp);
        Assert.Equal(100, p1.Rows.Count);
        Assert.NotNull(p1.Next);
        Assert.All(p1.Rows, r => Assert.Equal(LogLevel.Error, r.Level));

        var p2 = LogLevelFilter.ReadFile(path, p1.Next!.Value, LogLevel.Error | LogLevel.Warning, obs.Stamp);
        Assert.Equal(52, p2.Rows.Count);
        Assert.Null(p2.Next);
        Assert.Equal(LogLevel.Warning, p2.Rows[^2].Level);
        Assert.False(p2.Rows[^2].Continuation);
        Assert.Equal(LogLevel.Warning, p2.Rows[^1].Level);
        Assert.True(p2.Rows[^1].Continuation);

        File.AppendAllText(path, "2026-11-20 12:06:00 [ERR] appended later\n");
        var pAppended = LogLevelFilter.ReadFile(path, p1.Next!.Value, LogLevel.Error | LogLevel.Warning, obs.Stamp, expectedObservation: obs);
        Assert.Equal(52, pAppended.Rows.Count);

        File.WriteAllText(path, "2026-11-20 12:07:00 [ERR] rewritten\n");
        Assert.Throws<IOException>(() => LogLevelFilter.ReadFile(path, new FilterCursor(0), LogLevel.Error, obs.Stamp));
    }

    [Fact]
    public void EditorLogFilterProjectsLinesAndMapsOriginalNumbers()
    {
        string text =
            "2026-01-01 00:00:01 [INF] boot\r\n" +
            "2026-01-01 00:00:02 [ERR] first error\n" +
            "stack frame\r" +
            "2026-01-01 00:00:03 [WRN] heads up\n" +
            "2026-01-01 00:00:04 [ERR] second error\n";

        var filtered = EditorLogFilter.Filter(text, LogLevel.Error | LogLevel.Warning);
        Assert.Equal(
            "2026-01-01 00:00:02 [ERR] first error\n" +
            "2026-01-01 00:00:03 [WRN] heads up\n" +
            "2026-01-01 00:00:04 [ERR] second error",
            filtered.Text);
        Assert.Equal(new[] { 2, 4, 5 }, filtered.SourceLines);
        Assert.True(filtered.TryGetSourceLine(1, out int originalLine));
        Assert.Equal(4, originalLine);
        Assert.False(filtered.TryGetSourceLine(3, out _));
        Assert.Equal(string.Empty, EditorLogFilter.Filter(text, LogLevel.None).Text);
    }

    [Fact]
    public void LogIoPageEditPreservesPrefixSuffixAndDetectsExternalChanges()
    {
        string path = Path.Combine(_dir, "edit.log");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        byte[] content = Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\ndelta\n");
        File.WriteAllBytes(path, [.. bom, .. content]);

        var (obs, _) = LogFile.Observe(path, null);
        using var reader = LogReader.Open(path, TextEncoding.Utf8Bom);
        byte[] widths = reader.ReadMinimapWidths(bands: 32);
        Assert.NotEmpty(widths);

        var page1 = reader.RenderPage(0, new PageOptions(2, 1024));
        var page2 = reader.RenderPage(page1.NextOffset, new PageOptions(1, 1024));
        Assert.Equal("gamma", page2.Rows[0].Text);

        var snap2 = LogIo.ReadPageEdit(path, page2, reader.Encoding, obs.Stamp);
        Assert.Equal("gamma\n", snap2.OriginalText);
        var updatedStamp = LogIo.SavePageEdit(path, snap2, "GAMMA-EDITED\n");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path)[..3]);
        Assert.Equal("alpha\nbeta\nGAMMA-EDITED\ndelta\n", File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.pending"));

        // Page 1 at offset 0 also preserves the BOM without duplicating it.
        var snap1 = LogIo.ReadPageEdit(path, 0, page1.NextOffset, TextEncoding.Utf8Bom, updatedStamp);
        Assert.Equal("alpha\nbeta\n", snap1.OriginalText);
        var stampAfterPage1 = LogIo.SavePageEdit(path, snap1, "ALPHA\n");
        byte[] finalBytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, finalBytes[..3]);
        Assert.Equal("ALPHA\nGAMMA-EDITED\ndelta\n", Encoding.UTF8.GetString(finalBytes[3..]));

        // External rewrite causes SavePageEdit to reject the stale snapshot without modifying disk.
        File.WriteAllText(path, "EXTERNAL\n");
        Assert.Throws<IOException>(() => LogIo.SavePageEdit(path, snap1, "STALE\n"));
        Assert.Equal("EXTERNAL\n", File.ReadAllText(path));
        Assert.NotEqual(stampAfterPage1, LogFile.Observe(path, null).Observation.Stamp);
    }

    [Fact]
    public void ConsoleLineDeduplicatorHandlesAdvanceScrollLateEventsAndHistoryRewrite()
    {
        var dedup = new ConsoleLineDeduplicator();

        // Initial capture: 5 rows in a 6-row tall buffer, cursor at Y=3.
        string[] frame1 = ["line-0", "line-1", "line-2", "", "", ""];
        var snap1 = new ConsoleBufferSnapshot(80, 6, 0, 3, 0, 5);
        Assert.Equal(new[] { "line-0", "line-1", "line-2" }, dedup.ProcessSnapshot(snap1, frame1));

        // Normal cursor advance by 2 lines.
        string[] frame2 = ["line-0", "line-1", "line-2", "line-3", "line-4", ""];
        var snap2 = new ConsoleBufferSnapshot(80, 6, 0, 5, 0, 5);
        Assert.Equal(new[] { "line-3", "line-4" }, dedup.ProcessSnapshot(snap2, frame2, hasScrollTracker: true));

        // At bottom (CursorY == Height - 1 == 5): identical lines scrolled by 2 with scroll events.
        string[] frame3 = ["line-2", "line-3", "line-4", "repeat", "repeat", ""];
        var scrollEvents = new ConsoleEventBatch(
            Scroll: 2,
            ScrollCount: 1,
            FirstScrollDelta: -2,
            LayoutChanged: false,
            LeadingLayout: false,
            BottomRegionRows: 2,
            HistoryRedrawn: false,
            EventVersion: 1);
        Assert.Equal(new[] { "repeat", "repeat" }, dedup.ProcessSnapshot(snap2, frame3, scrollEvents, hasScrollTracker: true));

        // History rewritten above cursor without advancing or scrolling: emits nothing.
        string[] frame4 = ["EDITED", "line-3", "line-4", "repeat", "repeat", ""];
        var historyRewrite = new ConsoleEventBatch(0, 0, 0, false, false, 0, HistoryRedrawn: true, EventVersion: 2);
        Assert.Empty(dedup.ProcessSnapshot(snap2, frame4, historyRewrite, hasScrollTracker: true));

        // With a full buffer (> TailRows), scrolling 1 row leaves the previous 64-row tail intact
        // before the scroll event arrives; _scrollAlreadyCaptured absorbs the late event.
        var deepDedup = new ConsoleLineDeduplicator();
        const int deepHeight = ConsoleLineDeduplicator.TailRows + 6;
        const int deepEnd = deepHeight - 1;
        var deepSnap = new ConsoleBufferSnapshot(80, deepHeight, 0, deepEnd, 0, deepEnd);
        var deepFrame1 = Enumerable.Range(0, deepHeight).Select(i => i < deepEnd ? $"row-{i}" : "").ToArray();
        Assert.Equal(deepEnd, deepDedup.ProcessSnapshot(deepSnap, deepFrame1, default, hasScrollTracker: true).Length);

        var deepFrame2 = Enumerable.Range(0, deepHeight).Select(i => i < deepEnd - 1 ? $"row-{i + 1}" : i == deepEnd - 1 ? "new-row" : "").ToArray();
        Assert.Equal(new[] { "new-row" }, deepDedup.ProcessSnapshot(deepSnap, deepFrame2, default, hasScrollTracker: true));

        var lateScrollEvent = new ConsoleEventBatch(1, 1, -1, false, false, 1, false, EventVersion: 3);
        Assert.Empty(deepDedup.ProcessSnapshot(deepSnap, deepFrame2, lateScrollEvent, hasScrollTracker: true));
    }

    [Fact]
    public void ConsoleCaptureFindsTargetsExcludingSelf()
    {
        Assert.Empty(ConsoleCapture.FindTargetProcesses(Environment.ProcessId.ToString()));
        Assert.Empty(ConsoleCapture.FindTargetProcesses("99999999"));
        Assert.Empty(ConsoleCapture.FindTargetProcesses("nonexistent-process-xyz-12345"));
    }
}
