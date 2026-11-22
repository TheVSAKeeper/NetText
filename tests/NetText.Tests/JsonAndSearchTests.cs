using NetText.Core;
using Xunit;

namespace NetText.Tests;

public sealed class JsonFormatTests
{
    [Fact]
    public void FormatsWithoutRewritingLexemesOrReorderingKeys()
    {
        var source = "{\"z\":123456789012345678901234567890,\"a\":1e9000,\"z\":-0.00,\"s\":\"\\\"/ёж\\u2603\",\"empty\":{},\"arr\":[]}";
        Assert.True(JsonFormat.TryFormat(source, "\n", out var formatted, out var err), err);
        Assert.Contains("\"z\": 123456789012345678901234567890", formatted);
        Assert.Contains("\"a\": 1e9000", formatted);
        Assert.Contains("\"z\": -0.00", formatted);
        Assert.Contains("\"s\": \"\\\"/ёж\\u2603\"", formatted);
        Assert.Contains("\"empty\": {}", formatted);
        Assert.Contains("\"arr\": []", formatted);
        Assert.True(formatted.IndexOf("\"a\"", StringComparison.Ordinal) < formatted.LastIndexOf("\"z\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsInvalidJsonWithLocation()
    {
        var bad = "{\n \"a\": [1,]\n}";
        Assert.False(JsonFormat.TryFormat(bad, "\n", out _, out var err));
        Assert.Contains("строка 2, столбец 10", err);

        Assert.False(JsonFormat.TryFormat("{\"a\":01}", "\n", out _, out _));
        Assert.False(JsonFormat.TryFormat("{} false", "\n", out _, out _));
        Assert.False(JsonFormat.TryFormat("{\"a\":\"\\x00\"}", "\n", out _, out _));
    }

    [Fact]
    public void RetainsCrLfAndFormatsEmptyContainersAndRootValues()
    {
        Assert.True(JsonFormat.TryFormat("{\"a\":1}\r\n", "\r\n", out var formatted, out _));
        Assert.Equal("{\r\n  \"a\": 1\r\n}\r\n", formatted);

        Assert.True(JsonFormat.TryFormat("-0.00e+03", "\n", out var num, out _));
        Assert.Equal("-0.00e+03", num);

        Assert.True(JsonFormat.TryFormat("\"{[ , : ]}\"", "\n", out var str, out _));
        Assert.Equal("\"{[ , : ]}\"", str);
    }

    [Fact]
    public void RejectsEmptyInputAndReportsUnterminatedStrings()
    {
        Assert.False(JsonFormat.TryFormat("", "\n", out _, out var emptyErr));
        Assert.Contains("строка 1, столбец 1", emptyErr);

        Assert.False(JsonFormat.TryFormat("{\n\"abc", "\n", out _, out var untermErr));
        Assert.Contains("строка 2", untermErr);
    }

    [Fact]
    public void FormatsUtf8LosslesslyAndOpensEditorUnmanaged()
    {
        var source = "{\"z\":1e9000,\"empty\":{},\"s\":\"\\\"/ёж\"}\r\n"u8;
        Assert.True(JsonFormat.TryFormatUtf8(source, "\r\n"u8, out var formattedBytes, out var err), err);
        Assert.Equal("{\r\n  \"z\": 1e9000,\r\n  \"empty\": {},\r\n  \"s\": \"\\\"/ёж\"\r\n}\r\n", System.Text.Encoding.UTF8.GetString(formattedBytes));

        var tmp = Path.Combine(Path.GetTempPath(), "nt_open_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllBytes(tmp, [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'b']);
            using var opened = Files.OpenForEditor(tmp, windows1251: false);
            Assert.Equal(TextEncoding.Utf8Bom, opened.Format.Encoding);
            Assert.Equal(LineEndings.Cr, opened.Format.Endings);
            Assert.Equal("a\nb", System.Text.Encoding.UTF8.GetString(opened.Span));
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void FastUtf16FormatterMatchesUtf8EngineAndFallbacksOnAllEdgeCases()
    {
        string[] validCases =
        [
            "{}",
            "[]",
            "true",
            "false",
            "null",
            "\"x\"",
            "0",
            "-0.00",
            "1e9000",
            "0e+0",
            "-0.5E-2",
            "{\"z\":1,\"a\":[true,false,null,{},[]],\"z\":-0.00}",
            "{\"ru\":\"Привет 🙂\",\"esc\":\"\\\"\\\\\\/\\b\\f\\n\\r\\t\\u2603\\uD83D\\uDE42\"}",
            "{\"k\":1}\n",
            "{\"k\":1}\r\n",
            new string('[', 40) + "1" + new string(']', 40),
            new string('[', JsonFormat.MaxIndentDepth) + new string(']', JsonFormat.MaxIndentDepth),
        ];

        string[] newlines = ["\n", "\r\n", "\r\n--", "\n          "];
        foreach (var nl in newlines)
        {
            foreach (var input in validCases)
            {
                Assert.True(JsonFormat.TryFormat(input, nl, out var actual, out var actualErr), actualErr);
                Assert.True(
                    JsonFormat.TryFormatUtf8(
                        System.Text.Encoding.UTF8.GetBytes(input),
                        System.Text.Encoding.UTF8.GetBytes(nl),
                        out var expectedBytes,
                        out var expectedErr),
                    expectedErr);
                Assert.Equal(System.Text.Encoding.UTF8.GetString(expectedBytes), actual);
            }
        }

        Assert.True(JsonFormat.TryFormat("[0]", "\uD800", out var loneNlActual, out var loneNlErr), loneNlErr);
        Assert.True(JsonFormat.TryFormatUtf8("[0]"u8, System.Text.Encoding.UTF8.GetBytes("\uD800"), out var loneNlExpected, out _));
        Assert.Equal(System.Text.Encoding.UTF8.GetString(loneNlExpected), loneNlActual);

        string fallbackSurrogate = "\"a\uD800b\"";
        Assert.True(JsonFormat.TryFormat(fallbackSurrogate, "\n", out var repActual, out var repErr), repErr);
        Assert.True(
            JsonFormat.TryFormatUtf8(
                System.Text.Encoding.UTF8.GetBytes(fallbackSurrogate),
                "\n"u8,
                out var repExpected,
                out _));
        Assert.Equal(System.Text.Encoding.UTF8.GetString(repExpected), repActual);

        string[] invalidCases =
        [
            "",
            "   \r\n",
            "01",
            "-01",
            "1.",
            "1e+",
            "[1,]",
            "{\"a\":1,}",
            "\"a\nb\"",
            "\"a\u001Fb\"",
            "\"\\a\"",
            "\"\\u123G\"",
            "\"\\uD800\"",
            "\"\\uDC00\"",
            "\"\\uD800\\u0041\"",
            "\u00A0{}",
            "\uFEFF{}",
            "{} 1",
            "true false",
            new string('[', JsonFormat.MaxIndentDepth + 1) + new string(']', JsonFormat.MaxIndentDepth + 1),
        ];

        foreach (var input in invalidCases)
        {
            Assert.False(JsonFormat.TryFormat(input, "\n", out var actualOut, out var actualErr));
            Assert.Equal("", actualOut);
            Assert.False(JsonFormat.TryFormatUtf8(System.Text.Encoding.UTF8.GetBytes(input), "\n"u8, out _, out var expectedErr));
            Assert.Equal(expectedErr, actualErr);
        }

        Assert.False(
            JsonFormat.TryFormat(
                new string('[', JsonFormat.MaxIndentDepth + 1) + new string(']', JsonFormat.MaxIndentDepth + 1),
                "\n",
                out _,
                out var depthErr));
        Assert.Contains("Глубина вложенности JSON превышает 1024", depthErr);
    }
}

public sealed class EditorSearchTests
{
    [Fact]
    public void NavigatesTabsWithUnicodeWordsAndWrapsEmptyMatches()
    {
        var sources = new[]
        {
            new SearchSource(1, "котик КОТ"),
            new SearchSource(2, "кот"),
        };
        var query = new SearchQuery("кот", Regex: false, CaseSensitive: false, WholeWord: true, AllTabs: true);
        var origin = new SearchHit(1, new ByteRange("котик ".Length * 2 - 1, 0));
        // "котик " is 5 Cyrillic chars (10 bytes) + space (1 byte) = 11 bytes; "КОТ" is 6 bytes -> 11..17
        var exactOrigin = new SearchHit(1, new ByteRange(11, 17));
        Assert.True(EditorSearch.TryRun(query, SearchCommand.Next, sources, exactOrigin, true, "", out var outcome, out _));
        var found = Assert.IsType<SearchOutcome.Found>(outcome);
        Assert.Equal(2, found.Total);
        Assert.Equal(new SearchHit(2, new ByteRange(0, 6)), found.Hit);

        var zwQuery = new SearchQuery("^", Regex: true, CaseSensitive: false, WholeWord: false, AllTabs: true);
        Assert.True(EditorSearch.TryRun(zwQuery, SearchCommand.Next, sources, new SearchHit(2, new ByteRange(0, 0)), true, "", out var zwOut, out _));
        var zwFound = Assert.IsType<SearchOutcome.Found>(zwOut);
        Assert.Equal(new SearchHit(1, new ByteRange(0, 0)), zwFound.Hit);
    }

    [Fact]
    public void ReplacesCapturesAndTreatsLiteralReplacementsLiterally()
    {
        var regexSources = new[] { new SearchSource(1, "код=17, код=24") };
        var regexQuery = new SearchQuery(@"код=(\d+)", Regex: true, CaseSensitive: false, WholeWord: false, AllTabs: false);
        Assert.True(EditorSearch.TryRun(regexQuery, SearchCommand.ReplaceAll, regexSources, new SearchHit(1, new ByteRange(0, 0)), false, "[$1]", out var out1, out _));
        var rep1 = Assert.IsType<SearchOutcome.Replaced>(out1);
        Assert.Equal("[17], [24]", rep1.Text);
        Assert.Equal(2, rep1.Count);

        var litQuery = new SearchQuery("кот", Regex: false, CaseSensitive: false, WholeWord: false, AllTabs: false);
        Assert.True(EditorSearch.TryRun(litQuery, SearchCommand.Replace, new[] { new SearchSource(1, "кот") }, new SearchHit(1, new ByteRange(0, 0)), false, "$1", out var out2, out _));
        var rep2 = Assert.IsType<SearchOutcome.Replaced>(out2);
        Assert.Equal("$1", rep2.Text);
    }

    [Fact]
    public void ResumesBackwardAcrossTabsAndWrapsFromFirstHit()
    {
        var sources = new[] { new SearchSource(9, "x"), new SearchSource(3, "xx") };
        var query = new SearchQuery("x", Regex: false, CaseSensitive: false, WholeWord: false, AllTabs: true);
        Assert.True(EditorSearch.TryRun(query, SearchCommand.Previous, sources, new SearchHit(9, new ByteRange(0, 1)), true, "", out var outcome, out _));
        var found = Assert.IsType<SearchOutcome.Found>(outcome);
        Assert.Equal(3, found.Total);
        Assert.Equal(new SearchHit(3, new ByteRange(1, 2)), found.Hit);
    }

    [Fact]
    public void VerifiesLiteralPunctuationCaseSensitivityAndZeroWidthReplace()
    {
        var sources = new[] { new SearchSource(1, "ахб А.б а.б") };
        var query = new SearchQuery("а.б", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false);
        Assert.True(EditorSearch.TryRun(query, SearchCommand.Next, sources, new SearchHit(1, new ByteRange(0, 0)), false, "", out var out1, out _));
        var found = Assert.IsType<SearchOutcome.Found>(out1);
        Assert.Equal(1, found.Total);

        Assert.False(EditorSearch.TryRun(new SearchQuery("", false, false, false, false), SearchCommand.Next, sources, new SearchHit(1, new ByteRange(0, 0)), false, "", out _, out var emptyErr));
        Assert.Contains("Введите", emptyErr);

        Assert.False(EditorSearch.TryRun(new SearchQuery("(", true, false, false, false), SearchCommand.Next, sources, new SearchHit(1, new ByteRange(0, 0)), false, "", out _, out var badRe));
        Assert.Contains("Ошибка регулярного выражения", badRe);

        var zwSources = new[] { new SearchSource(1, "ёж\nкот") };
        Assert.True(EditorSearch.TryRun(new SearchQuery("(?m)^", true, false, false, false), SearchCommand.ReplaceAll, zwSources, new SearchHit(1, new ByteRange(0, 0)), false, "> ", out var zwOut, out _));
        var rep = Assert.IsType<SearchOutcome.Replaced>(zwOut);
        Assert.Equal("> ёж\n> кот", rep.Text);
        Assert.Equal(2, rep.Count);
    }

    [Fact]
    public void Utf8SearchMatchesAndReplacesWithoutStringAllocation()
    {
        byte[] doc1 = System.Text.Encoding.UTF8.GetBytes("котик КОТ котик");
        byte[] doc2 = System.Text.Encoding.UTF8.GetBytes("кот");
        var sources = new[]
        {
            new SearchUtf8Source(1, doc1, doc1.Length),
            new SearchUtf8Source(2, doc2, doc2.Length),
        };

        var litQuery = new SearchQuery("котик", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: true);
        Assert.True(EditorSearch.TryRunUtf8(litQuery, SearchCommand.Next, sources, new SearchHit(1, new ByteRange(0, 10)), true, "", out var foundOut, out _));
        var found = Assert.IsType<SearchOutcome.Found>(foundOut);
        Assert.Equal(2, found.Total);
        Assert.Equal(new SearchHit(1, new ByteRange(18, 28)), found.Hit);

        var repQuery = new SearchQuery("котик", Regex: false, CaseSensitive: true, WholeWord: false, AllTabs: false);
        Assert.True(EditorSearch.TryRunUtf8(repQuery, SearchCommand.ReplaceAll, sources, new SearchHit(1, new ByteRange(0, 0)), false, "лис", out var repOut, out _));
        var rep = Assert.IsType<SearchOutcome.ReplacedUtf8>(repOut);
        try
        {
            Assert.Equal(2, rep.Count);
            Assert.Equal("лис КОТ лис", System.Text.Encoding.UTF8.GetString(rep.Buffer, 0, rep.Length));
        }
        finally
        {
            if (rep.FromPool) System.Buffers.ArrayPool<byte>.Shared.Return(rep.Buffer);
        }

        var icQuery = new SearchQuery("кот", Regex: false, CaseSensitive: false, WholeWord: true, AllTabs: true);
        Assert.True(EditorSearch.TryRunUtf8(icQuery, SearchCommand.Previous, sources, new SearchHit(2, new ByteRange(0, 0)), false, "", out var icOut, out _));
        var icFound = Assert.IsType<SearchOutcome.Found>(icOut);
        Assert.Equal(2, icFound.Total);
        Assert.Equal(1UL, icFound.Hit!.Value.Tab);
    }

    [Fact]
    public void JsonFormatRejectsLoneSurrogateEscapes()
    {
        Assert.False(JsonFormat.TryFormat("{\"a\":\"\\uD800\"}", "\n", out _, out var err1));
        Assert.Contains("Ошибка JSON", err1);
        Assert.False(JsonFormat.TryFormatUtf8("{\"\\uDC00\":1}"u8, "\n"u8, out _, out var err2));
        Assert.Contains("Ошибка JSON", err2);
        Assert.True(JsonFormat.TryFormatUtf8("{\"ok\":\"\\uD83D\\uDE42\"}"u8, "\n"u8, out _, out _));
    }
}
