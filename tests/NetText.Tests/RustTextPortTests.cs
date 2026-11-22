using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NetText.Core;
using NetText.UI;
using Xunit;

namespace NetText.Tests;

public sealed class RustTextPortTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("NetText-port-").FullName;

    [Fact]
    public void GeneratesTemplateAndPreservesDestinationWhenCancelled()
    {
        string path = Path.Combine(_root, "output.txt");
        File.WriteAllText(path, "keep");
        var template = new LineTemplate("{int_from_-1} {literal} {rand_string_0}", 3);
        using (var cancellation = new CancellationTokenSource())
        {
            Assert.Throws<OperationCanceledException>(() =>
                template.WriteFile(FileTarget.FromPicker(path), cancellation.Token, _ => cancellation.Cancel()));
        }
        Assert.Equal("keep", File.ReadAllText(path));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root), item => item.EndsWith(".tmp"));
        template.WriteFile(FileTarget.FromPicker(path), CancellationToken.None);
        Assert.Equal("-1 {literal} \n0 {literal} \n1 {literal} ", File.ReadAllText(path));
        Assert.Throws<OverflowException>(() => new LineTemplate("{int_from_9223372036854775807}", 2));
    }

    [Fact]
    public void ComparisonKeepsOrderCaseWhitespaceAndBlankLines()
    {
        var result = ListComparison.Compare("Я\r\n\r\na\ra\n a\n", "a\nЯ\nb\n\nА");
        Assert.Equal(["Я", "", "a"], result.Common);
        Assert.Equal([" a"], result.OnlyLeft);
        Assert.Equal(["b", "А"], result.OnlyRight);
    }

    [Fact]
    public void HistoryExtendsPastRecentMenuAndReadsLegacyEntries()
    {
        string path = Path.Combine(_root, "history.json");
        File.WriteAllText(path, """[{"path":"legacy","windows_1251":true}]""");
        var history = new RecentFiles(path);
        history.Reload();
        Assert.True(history.Entries[0].Windows1251);
        history.Record(Enumerable.Range(0, 21).Select(i => new RecentFile($"file-{i}", false, Lines: (ulong)i)));
        var reread = new RecentFiles(path);
        reread.Reload();
        Assert.Equal(16, reread.Entries.Count);
        Assert.Equal(22, reread.History.Count);
        Assert.Equal(20UL, reread.History[0].Lines);
        Assert.Equal("legacy", reread.History[^1].Path);
    }

    [Fact]
    public void HistoryUsesRelativeTimeUntilThePreviousDay()
    {
        var now = new DateTimeOffset(2026, 11, 18, 13, 00, 00, TimeSpan.Zero);
        Assert.Equal(" · 1 стр. · только что", MainWindow.HistoryMetadata(1, now.AddSeconds(-59), now));
        Assert.Equal(" · 1 мин назад", MainWindow.HistoryMetadata(null, now.AddMinutes(-1), now));
        Assert.Equal(" · 1 ч назад", MainWindow.HistoryMetadata(null, now.AddHours(-1), now));
        Assert.Contains("17.11.2026", MainWindow.HistoryMetadata(null, now.AddDays(-1), now));
    }

    [Fact]
    public void SnapshotsDeduplicateAndValidateTheStoredBytes()
    {
        string path = Path.Combine(_root, "source.txt");
        string directory = Path.Combine(_root, "versions");
        var store = new FileVersions(directory);
        var settings = new EditorSettings();
        File.WriteAllText(path, "one");
        var target = FileTarget.FromPicker(path);
        store.Capture(target, TextEncoding.Utf8, settings);
        store.Capture(target, TextEncoding.Utf8, settings);
        var snapshot = Assert.Single(store.List(path));
        Assert.Equal("one", store.Read(snapshot).Text);
        File.WriteAllText(Path.Combine(directory, snapshot.Id + ".bin"), "two");
        Assert.Throws<InvalidDataException>(() => store.Read(snapshot));
    }

    [Fact]
    public void FileSavePreservesAlternativeStreamsAndRefusesAnActiveWriter()
    {
        string path = Path.Combine(_root, "source.txt");
        File.WriteAllText(path, "one");
        File.WriteAllText(path + ":origin", "metadata");
        var target = FileTarget.FromPicker(path);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            Assert.Throws<IOException>(() => Files.SaveText(target, TextFormat.Default, "two"));
        Assert.Equal("one", File.ReadAllText(path));
        var saved = Files.SaveText(target, TextFormat.Default, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal("metadata", File.ReadAllText(path + ":origin"));
        Assert.Equal(target.Version!.Value.CreatedUtc, saved.Version!.Value.CreatedUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimestampSeekHandlesNanosecondsUndatedLinesAndUtf16(bool utf16)
    {
        string path = Path.Combine(_root, "chronological.log");
        Encoding encoding = utf16 ? Encoding.Unicode : new UTF8Encoding(false);
        string prefix = "2026-11-17 01:30:20.085000001 first\r\ncontinuation\r\n";
        string line = "2026-11-17T01:30:20.085000003 second\r\n";
        File.WriteAllText(path, prefix + line + "2026-11-17 01:33:42.8750 third", encoding);
        using var reader = LogReader.Open(path, null);
        ulong second = (ulong)(encoding.GetPreamble().Length + encoding.GetByteCount(prefix));
        ulong third = second + (ulong)encoding.GetByteCount(line);
        Assert.Equal(second, LogTimestamp.Seek(reader, 0, 0, "2026-11-17 01:30:20.085000002", CancellationToken.None));
        Assert.Equal(third, LogTimestamp.Seek(reader, 0, second, "01:33:42.8750", CancellationToken.None));
        Assert.Null(LogTimestamp.Seek(reader, 0, 0, "2026-11-18 00:00:00", CancellationToken.None));
        Assert.Throws<FormatException>(() => LogTimestamp.Seek(reader, 0, 0, "01:60:01", CancellationToken.None));
    }

    [Fact]
    public void LogSearchRetriesTheSamePhysicalLineWhenAUtf8TailIsCompleted()
    {
        string file = Path.Combine(_root, "tail.log");
        File.WriteAllBytes(file, [.. "first\nlast "u8.ToArray(), 0xD1]);
        var query = new LogSearchQuery("я", false, true, false);
        var pending = LogSearch.SearchPage(file, null, query, 0, 5, LogSearchLimits.Default, CancellationToken.None);
        Assert.False(pending.Complete);
        Assert.Equal(6UL, pending.PendingLineStart);
        Assert.Equal(6UL, pending.NextOffset);
        Assert.Empty(pending.Hits);

        using (var append = File.Open(file, FileMode.Append)) append.WriteByte(0x8F);
        var completed = LogSearch.SearchPage(file, null, query, pending.NextOffset, 5, LogSearchLimits.Default, CancellationToken.None);
        Assert.True(completed.Complete);
        Assert.Null(completed.PendingLineStart);
        Assert.Equal(6UL, Assert.Single(completed.Hits).LineStart);
    }

    [Fact]
    public void LogBookmarkAcceptsAnAppendButRejectsARewriteAndLegacyOffset()
    {
        string file = Path.Combine(_root, "bookmark.log");
        File.WriteAllText(file, "a\nb\nc\n");
        var (before, _) = LogFile.Observe(file, null);
        var bookmark = new LogBookmark { Offset = 4, Hidden = 2 };
        bookmark.Record(before);
        File.AppendAllText(file, "d\n");
        using (var reader = LogReader.Open(file, null))
        {
            var (appended, _) = LogFile.ObserveHandle(file, reader.Handle, before);
            Assert.True(bookmark.CanRestore(reader.Handle, appended));
            var legacy = bookmark.Copy();
            legacy.Fingerprint = null;
            Assert.False(legacy.CanRestore(reader.Handle, appended));
        }

        File.WriteAllText(file, "X\nb\nc\nd\n");
        using var rewritten = LogReader.Open(file, null);
        var (after, _) = LogFile.ObserveHandle(file, rewritten.Handle, null);
        Assert.False(bookmark.CanRestore(rewritten.Handle, after));
    }

    [Fact]
    public void SessionViewUpdatesDoNotRewriteUnchangedDraftAndEmptySessionRestores()
    {
        string directory = Path.Combine(_root, "session");
        byte[] draft = "draft"u8.ToArray();
        var tab = new SessionTab(1, null, new SessionFormat("Utf8", "Lf", false), new SessionSelection(0, 0),
            false, [0, 0], 1, null, Group: 1, JsonDisabled: true);
        var state = new SessionState([tab], 0, new SessionWindow(1000, 700, false), true, 15,
            Panes: new SessionPane(1));
        using (var session = SessionJournal.Open(directory))
        {
            session.Commit(1, new SessionCapture(state, [draft]));
            var moved = tab with { Selection = new SessionSelection(2, 4), Scroll = [4, 8] };
            session.Commit(2, new SessionCapture(state with { Tabs = [moved] }, [draft]));
        }
        var first = Assert.Single(Directory.GetDirectories(directory, "checkpoint-*"));
        Assert.True(File.Exists(Path.Combine(first, "view.json")));
        using (var session = SessionJournal.Open(directory))
        {
            var restored = session.Restore()!;
            Assert.Equal("draft", Assert.Single(restored.DraftTexts));
            Assert.Equal(new SessionSelection(2, 4), restored.State.Tabs[0].Selection);
            Assert.True(restored.State.Tabs[0].JsonDisabled);
            Assert.Equal(1, restored.State.Panes?.Group);
            session.Commit(1, new SessionCapture(state with { Tabs = [], Panes = null }, []));
        }
        using var last = SessionJournal.Open(directory);
        Assert.Empty(last.Restore()!.State.Tabs);
    }

    [Fact]
    public void TextSearchListsOnlyTheFirstHundredAndCountsTheRest()
    {
        var buffer = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ё abc\n", 117)));
        Assert.True(EditorSearch.TryRunUtf8(new SearchQuery("ё", false, false, false, false), SearchCommand.List,
            [new SearchUtf8Source(1, buffer, buffer.Length)], new SearchHit(1, new ByteRange(0, 0)),
            false, "", out var outcome, out _));
        var found = Assert.IsType<SearchOutcome.Found>(outcome);
        Assert.Equal(117, found.Total);
        Assert.Equal(100, found.Results!.Count);
        Assert.Equal(new ByteRange(0, 2), found.Results[0].Range);
    }

    [Fact]
    public void BackwardsNavigationResumesAndSelectsTheLastMatchBeforeTheCaret()
    {
        var buffer = "я я я"u8.ToArray();
        var sources = new[] { new SearchUtf8Source(1, buffer, buffer.Length) };
        var query = new SearchQuery("я", false, true, false, false);
        var origin = new SearchHit(1, new ByteRange(buffer.Length, buffer.Length));
        Assert.True(EditorSearch.TryNavigateUtf8(query, true, sources, origin, false, null,
            out var first, out _, matchBatch: 1));
        var paused = Assert.IsType<SearchOutcome.Navigated>(first);
        Assert.NotNull(paused.Cursor);
        Assert.Null(paused.Hit);
        Assert.True(EditorSearch.TryNavigateUtf8(query, true, sources, origin, false, paused.Cursor,
            out var second, out _));
        var finished = Assert.IsType<SearchOutcome.Navigated>(second);
        Assert.Equal(new SearchHit(1, new ByteRange(6, 8)), finished.Hit);
        Assert.Null(finished.Cursor);
    }

    [Fact]
    public void JsonValidationReportsUnicodeColumnAndTheExactByteOffset()
    {
        var text = """{"я":1,}"""u8;
        Assert.False(JsonFormat.TryValidate(text, out string error, out int position));
        Assert.Equal(text.Length - 1, position);
        Assert.Contains("столбец 8", error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasePackageRequiresChecksumAndOnlyKnownPaths(bool legacy)
    {
        string archive = Path.Combine(_root, "NetText.zip");
        string stage = Path.Combine(_root, "stage");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            string[] names = legacy
                ? ["NetText.exe", "Scintilla.dll", "Lexilla.dll", "Scintilla-License.txt", "README.md", "docs/comparison.md", "docs/comparison-raw.json"]
                : ["NetText.exe", "Scintilla-License.txt", "README.md", "docs/comparison.md", "docs/comparison-raw.json"];
            foreach (string name in names)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }
        }
        string Checksum() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))) + "  NetText.zip";
        Assert.Throws<InvalidDataException>(() => Updates.VerifyAndStage(archive, new string('0', 64) + " NetText.zip", stage));
        Assert.Equal(legacy ? 4 : 1, Updates.VerifyAndStage(archive, Checksum(), stage).Length);
        Assert.Equal("NetText.exe", File.ReadAllText(Path.Combine(stage, "new-NetText.exe")));
        Assert.Equal(legacy, File.Exists(Path.Combine(stage, "new-Scintilla.dll")));
        if (!legacy)
        {
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            {
                using var entry = zip.CreateEntry("Scintilla.dll").Open();
                entry.WriteByte(1);
            }
            Assert.Throws<InvalidDataException>(() => Updates.VerifyAndStage(archive, Checksum(), stage));
            using var clean = ZipFile.Open(archive, ZipArchiveMode.Update);
            clean.GetEntry("Scintilla.dll")!.Delete();
        }
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            using var entry = zip.CreateEntry("../bad.exe").Open();
            entry.WriteByte(1);
        }
        Assert.Throws<InvalidDataException>(() => Updates.VerifyAndStage(archive, Checksum(), stage));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
