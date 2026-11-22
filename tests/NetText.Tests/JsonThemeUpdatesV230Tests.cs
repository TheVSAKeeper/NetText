using System.Text;
using System.Text.Json;
using NetText.Core;
using NetText.UI;
using Xunit;

namespace NetText.Tests;

public sealed class JsonThemeUpdatesV230Tests
{
    [Fact]
    public void JsonFormatDetectsJsonlAndDiscriminatesLogFiles()
    {
        Assert.True(JsonFormat.IsJsonLinesPath("events.jsonl"));
        Assert.True(JsonFormat.IsJsonLinesPath("stream.NDJSON"));
        Assert.False(JsonFormat.IsJsonLinesPath("data.json"));
        Assert.False(JsonFormat.IsJsonLinesPath("app.log"));

        Assert.True(JsonFormat.IsJsonContainerPrefix("  {\"a\":1}"u8));
        Assert.True(JsonFormat.IsJsonContainerPrefix(" [1, 2]"u8));
        Assert.False(JsonFormat.IsJsonContainerPrefix("{not json}"u8));
        Assert.False(JsonFormat.IsJsonContainerPrefix("2026-11-20 INFO started"u8));

        var regularLogWithBrace = "{startup} first line\n2026-11-20 INFO running\n"u8;
        Assert.False(JsonFormat.LooksLikeJson("app.log", regularLogWithBrace));

        var singleJsonInLog = "{\"a\":1}\n2026-11-20 INFO running\n"u8;
        Assert.False(JsonFormat.LooksLikeJson("app.log", singleJsonInLog));

        var jsonLinesInLog = "{\"a\":1}\n{\"b\":2}\n"u8;
        Assert.True(JsonFormat.LooksLikeJson("app.log", jsonLinesInLog));
        Assert.True(JsonFormat.UsesJsonLines("app.log", jsonLinesInLog));

        var plainText = "hello world"u8;
        Assert.False(JsonFormat.LooksLikeJson("notes.txt", plainText));
        Assert.True(JsonFormat.LooksLikeJson(null, "\uFEFF  {\"ok\":true}"u8));
    }

    [Fact]
    public void JsonFormatValidatesPerLineAndFormatsJsonlCompactly()
    {
        var validJsonl = "{\"id\":1}\n{\"id\":2,\"name\":\"тест\"}\n"u8;
        Assert.True(JsonFormat.TryValidate(validJsonl, "items.jsonl", out string okErr, out int okOffset, out int okLine), okErr);
        Assert.Equal(-1, okOffset);

        var badJsonl = "{\"id\":1}\n{\"id\":}\n"u8;
        Assert.False(JsonFormat.TryValidate(badJsonl, "items.jsonl", out string badErr, out int badOffset, out int badLine));
        Assert.Equal(2, badLine);
        Assert.Contains("строка 2", badErr);
        Assert.True(badOffset >= "{\"id\":1}\n".Length);

        var prettyJsonl = "{ \"z\" : 1 , \"a\" : [ 2 , 3 ] }\r\n{ \"ok\" : true }\r\n";
        Assert.True(JsonFormat.TryFormat(prettyJsonl, "items.jsonl", null, "\r\n", out string compact, out string fmtErr), fmtErr);
        Assert.Equal("{\"z\":1,\"a\":[2,3]}\r\n{\"ok\":true}\r\n", compact);

        Assert.True(JsonFormat.TryFormat("{\"a\":1}", "data.json", null, "\n", out string indented, out string fmtJsonErr), fmtJsonErr);
        Assert.Equal("{\n  \"a\": 1\n}", indented);
    }

    [Fact]
    public void ThemePaletteSwitchesThemesAndMapsCompatibilityTokens()
    {
        try
        {
            ThemePalette.Apply(ThemeKind.Focus);
            Assert.Equal(ThemeKind.Focus, ThemePalette.CurrentKind);
            Assert.Equal("focus", ThemePalette.CurrentName);
            Assert.Equal(0x413830u, ThemePalette.Editor);
            Assert.Equal(ThemePalette.Editor, ThemePalette.Bg);
            Assert.Equal(0x27221Du, ThemePalette.Chrome);
            Assert.Equal(ThemePalette.Chrome, ThemePalette.TabBar);
            Assert.False(ThemePalette.HasTitleMotif);

            Assert.True(ThemePalette.Apply("neon"));
            Assert.Equal(ThemeKind.Neon, ThemePalette.CurrentKind);
            Assert.Equal("neon", ThemePalette.CurrentName);
            Assert.Equal(0x261410u, ThemePalette.Editor);
            Assert.Equal(0xFFE622u, ThemePalette.Accent);
            Assert.Equal(0xFFF271u, ThemePalette.EditorCaret);
            Assert.Equal(0xFFDF8Cu, ThemePalette.JsonKey);
            Assert.False(ThemePalette.HasTitleMotif);

            Assert.True(ThemePalette.Apply("vicecity"));
            Assert.Equal(ThemeKind.ViceCity, ThemePalette.CurrentKind);
            Assert.Equal("vicecity", ThemePalette.CurrentName);
            Assert.Equal(0x241519u, ThemePalette.Editor);
            Assert.Equal(0xAD62FFu, ThemePalette.Accent);
            Assert.Equal(0x211319u, ThemePalette.TitleMotif);
            Assert.Equal(0x492349u, ThemePalette.TitleGlow);
            Assert.True(ThemePalette.HasTitleMotif);
        }
        finally
        {
            ThemePalette.Apply(ThemeKind.Focus);
        }
    }

    [Fact]
    public void EditorSearchFindAllRangesHandlesUtf8AndRespectsLimit()
    {
        var utf8 = Encoding.UTF8.GetBytes("кот и Кот и котик");
        var query = new SearchQuery("кот", Regex: false, CaseSensitive: false, WholeWord: true, AllTabs: false);
        var result = EditorSearch.FindAllRanges(utf8, query, EditorSearch.MaxMultiCaretMatches, TimeSpan.FromMilliseconds(400));
        Assert.Null(result.Error);
        Assert.False(result.TimedOut);
        Assert.False(result.Truncated);
        Assert.Equal(2, result.TotalMatches);
        Assert.Equal(new ByteRange(0, 6), result.Ranges[0]);
        Assert.Equal(new ByteRange(10, 16), result.Ranges[1]);

        var many = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a ", 10_005)));
        var litQuery = new SearchQuery("a", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false);
        var limited = EditorSearch.FindAllRanges(many, litQuery, EditorSearch.MaxMultiCaretMatches, TimeSpan.FromMilliseconds(1000));
        Assert.True(limited.Truncated);
        Assert.Equal(EditorSearch.MaxMultiCaretMatches, limited.Ranges.Count);
        Assert.Equal(10_005, limited.TotalMatches);
    }

    [Fact]
    public void EditorSearchLiteralReplaceAllAndSingleHandlesEdgeCasesAndLargeMatchCounts()
    {
        var query = new SearchQuery("альфа", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false);
        SearchSource[] sources = [new SearchSource(1, "альфа бета альфа")];

        Assert.True(EditorSearch.TryRun(
            new SearchQuery("нет", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false),
            SearchCommand.ReplaceAll,
            sources,
            new SearchHit(1, new ByteRange(0, 0)),
            false,
            "x",
            out var noMatch,
            out var noMatchErr), noMatchErr);
        Assert.IsType<SearchOutcome.NoReplacements>(noMatch);

        Assert.True(EditorSearch.TryRun(
            query,
            SearchCommand.Replace,
            sources,
            new SearchHit(1, new ByteRange(11, 11)),
            false,
            "$1",
            out var secondSingle,
            out var secondErr), secondErr);
        var secondRep = Assert.IsType<SearchOutcome.Replaced>(secondSingle);
        Assert.Equal(new ByteRange(20, 30), secondRep.Range);
        Assert.Equal("$1", secondRep.Text);
        Assert.Equal(1, secondRep.Count);

        Assert.True(EditorSearch.TryRun(
            query,
            SearchCommand.Replace,
            sources,
            new SearchHit(1, new ByteRange(25, 25)),
            false,
            "$1",
            out var wrappedSingle,
            out var wrappedErr), wrappedErr);
        var wrappedRep = Assert.IsType<SearchOutcome.Replaced>(wrappedSingle);
        Assert.Equal(new ByteRange(0, 10), wrappedRep.Range);
        Assert.Equal("$1", wrappedRep.Text);
        Assert.Equal(1, wrappedRep.Count);

        Assert.True(EditorSearch.TryRun(
            new SearchQuery("1234", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false),
            SearchCommand.ReplaceAll,
            [new SearchSource(1, "xx-1234-xx-1234-xx")],
            new SearchHit(1, new ByteRange(0, 0)),
            false,
            "z",
            out var shrinkOut,
            out var shrinkErr), shrinkErr);
        var shrinkRep = Assert.IsType<SearchOutcome.Replaced>(shrinkOut);
        Assert.Equal(2, shrinkRep.Count);
        Assert.Equal("xx-z-xx-z-xx", shrinkRep.Text);
        Assert.Equal(new ByteRange(0, 18), shrinkRep.Range);

        Assert.True(EditorSearch.TryRun(
            new SearchQuery("a", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false),
            SearchCommand.ReplaceAll,
            [new SearchSource(1, "a-a")],
            new SearchHit(1, new ByteRange(0, 0)),
            false,
            "xyz",
            out var growOut,
            out var growErr), growErr);
        var growRep = Assert.IsType<SearchOutcome.Replaced>(growOut);
        Assert.Equal(2, growRep.Count);
        Assert.Equal("xyz-xyz", growRep.Text);
        Assert.Equal(new ByteRange(0, 3), growRep.Range);

        Assert.True(EditorSearch.TryRun(
            new SearchQuery("ab", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false),
            SearchCommand.ReplaceAll,
            [new SearchSource(1, "ababab")],
            new SearchHit(1, new ByteRange(0, 0)),
            false,
            "",
            out var clearOut,
            out var clearErr), clearErr);
        var clearRep = Assert.IsType<SearchOutcome.Replaced>(clearOut);
        Assert.Equal(3, clearRep.Count);
        Assert.Equal("", clearRep.Text);
        Assert.Equal(new ByteRange(0, 6), clearRep.Range);

        Assert.True(EditorSearch.TryRun(
            new SearchQuery("a.b[0]", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false),
            SearchCommand.ReplaceAll,
            [new SearchSource(1, "a.b[0] aXb[0] $1")],
            new SearchHit(1, new ByteRange(0, 0)),
            false,
            "$1",
            out var metaOut,
            out var metaErr), metaErr);
        var metaRep = Assert.IsType<SearchOutcome.Replaced>(metaOut);
        Assert.Equal(1, metaRep.Count);
        Assert.Equal("$1 aXb[0] $1", metaRep.Text);

        string manyInput = string.Concat(Enumerable.Repeat("k,", 20_000));
        string expectedMany = string.Concat(Enumerable.Repeat("mm,", 20_000));
        Assert.True(EditorSearch.TryRun(
            new SearchQuery("k", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false),
            SearchCommand.ReplaceAll,
            [new SearchSource(1, manyInput)],
            new SearchHit(1, new ByteRange(0, 0)),
            false,
            "mm",
            out var manyOut,
            out var manyErr), manyErr);
        var manyRep = Assert.IsType<SearchOutcome.Replaced>(manyOut);
        Assert.Equal(20_000, manyRep.Count);
        Assert.Equal(new ByteRange(0, Encoding.UTF8.GetByteCount(manyInput)), manyRep.Range);
        Assert.Equal(expectedMany, manyRep.Text);

        Assert.False(EditorSearch.TryRun(
            new SearchQuery("", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false),
            SearchCommand.ReplaceAll,
            sources,
            new SearchHit(1, new ByteRange(0, 0)),
            false,
            "x",
            out var emptyOut,
            out var emptyErr));
        Assert.IsType<SearchOutcome.NoReplacements>(emptyOut);
        Assert.Equal("Введите текст или регулярное выражение.", emptyErr);

        Assert.False(EditorSearch.TryRun(
            new SearchQuery(new string('б', 2049), Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false),
            SearchCommand.Replace,
            sources,
            new SearchHit(1, new ByteRange(0, 0)),
            false,
            "x",
            out var bigOut,
            out var bigErr));
        Assert.IsType<SearchOutcome.NoReplacements>(bigOut);
        Assert.Equal("Запрос поиска превышает 4 КиБ.", bigErr);

        var emojiSources = new[] { new SearchSource(1, "\uD83D\uDE00\uD83D\uDE00") };
        Assert.True(EditorSearch.TryRun(
            new SearchQuery("\uD83D", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false),
            SearchCommand.Replace,
            emojiSources,
            new SearchHit(1, new ByteRange(4, 4)),
            false,
            "x",
            out var splitSurrOut,
            out var splitSurrErr), splitSurrErr);
        Assert.IsType<SearchOutcome.NoReplacements>(splitSurrOut);

        var rawLoneSources = new[] { new SearchSource(1, "a\uD83Db") };
        Assert.True(EditorSearch.TryRun(
            new SearchQuery("\uD83D", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false),
            SearchCommand.Replace,
            rawLoneSources,
            new SearchHit(1, new ByteRange(0, 0)),
            false,
            "x",
            out var rawLoneOut,
            out var rawLoneErr), rawLoneErr);
        var rawLoneRep = Assert.IsType<SearchOutcome.Replaced>(rawLoneOut);
        Assert.Equal(new ByteRange(1, 4), rawLoneRep.Range);
    }

    [Fact]
    public void UpdatesValidatesReleaseUrlsAndDeserializesNotesAndHistory()
    {
        Assert.True(Updates.IsAllowedReleaseUrl("https://github.com/TheVSAKeeper/NetText/releases/tag/v2.3.0"));
        Assert.True(Updates.IsAllowedReleaseUrl("https://github.com/TheVSAKeeper/NetText/releases/download/v2.3.0/NetText.zip"));
        Assert.False(Updates.IsAllowedReleaseUrl("http://github.com/TheVSAKeeper/NetText/releases/tag/v2.3.0"));
        Assert.False(Updates.IsAllowedReleaseUrl("https://github.com/Other/NetText/releases/tag/v2.3.0"));
        Assert.False(Updates.IsAllowedReleaseUrl("https://evil.example/NetText.zip"));
        Assert.False(Updates.IsAllowedReleaseUrl("https://github.com/TheVSAKeeper/NetText/releases/tag/../v2.3.0"));

        const string singleJson = """
            {
              "tag_name": "v2.3.0",
              "draft": false,
              "prerelease": false,
              "html_url": "https://github.com/TheVSAKeeper/NetText/releases/tag/v2.3.0",
              "body": "## Changes\n- Multi-caret",
              "assets": [
                { "name": "NetText.zip", "browser_download_url": "https://github.com/TheVSAKeeper/NetText/releases/download/v2.3.0/NetText.zip", "size": 1024 }
              ]
            }
            """;
        var single = JsonSerializer.Deserialize(singleJson, ReleaseJson.Default.StableRelease);
        Assert.NotNull(single);
        Assert.Equal("v2.3.0", single!.Tag);
        Assert.Equal("## Changes\n- Multi-caret", single.Body);
        Assert.Equal("https://github.com/TheVSAKeeper/NetText/releases/tag/v2.3.0", single.HtmlUrl);

        const string arrayJson = """
            [
              {
                "tag_name": "v2.3.0",
                "draft": false,
                "prerelease": false,
                "html_url": "https://github.com/TheVSAKeeper/NetText/releases/tag/v2.3.0",
                "body": "Notes 2.3.0",
                "assets": []
              },
              {
                "tag_name": "v2.2.0",
                "draft": false,
                "prerelease": false,
                "html_url": "https://github.com/TheVSAKeeper/NetText/releases/tag/v2.2.0",
                "body": "Notes 2.2.0",
                "assets": []
              }
            ]
            """;
        var list = JsonSerializer.Deserialize(arrayJson, ReleaseJson.Default.StableReleaseArray);
        Assert.NotNull(list);
        Assert.Equal(2, list!.Length);
        Assert.Equal("Notes 2.3.0", list[0].Body);
        Assert.Equal("Notes 2.2.0", list[1].Body);
    }

    [Fact]
    public async Task FileWatcherTriggersOnChangeAndRetriesIoExceptions()
    {
        string dir = Directory.CreateTempSubdirectory("NetText-watch-").FullName;
        try
        {
            string file = Path.Combine(dir, "watched.txt");
            File.WriteAllText(file, "v1");

            var triggered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var watch = new EditorFileWatch(file, () => triggered.TrySetResult(true)))
            {
                await Task.Delay(50);
                File.WriteAllText(file, "v2");
                var completed = await Task.WhenAny(triggered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.Same(triggered.Task, completed);
                Assert.True(await triggered.Task);
            }

            int attempts = 0;
            string readResult = await EditorFileWatch.ReadWithRetryAsync(_ =>
            {
                attempts++;
                if (attempts < 3) throw new IOException("locked");
                return Task.FromResult("ok");
            }, delayMs: 10);
            Assert.Equal("ok", readResult);
            Assert.Equal(3, attempts);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
