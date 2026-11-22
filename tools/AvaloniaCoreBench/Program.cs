using System.Diagnostics;
using System.Globalization;
using System.Text;
using NetText.Core;
using RustText.Demo;
using RustText.Demo.Editor;
using RustText.Demo.Logs;
using NetSearchHit = NetText.Core.SearchHit;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

int iterations = int.Parse(args[0]);
int targetBytes = int.Parse(args[1]);
string logPath = args[2];
LogFixture fixture = CreateLog(logPath, targetBytes);
using (var file = File.OpenRead(logPath))
{
    Console.WriteLine($"fixture\tlog\t{file.Length}\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file))}");
}

Measure("log-index", () =>
{
    using var file = LogDataReader.Open(logPath);
    var reader = new LogDataReader(file, TextEncodingKind.Utf8);
    var checkpoint = reader.ScanLineBreaks(new LogLineCheckpoint(0, 0), file.Length, null, CancellationToken.None);
    return (checkpoint.Offset, checkpoint.Lines);
}, () =>
{
    using var reader = LogReader.Open(logPath, null);
    reader.Index.Advance(reader.Handle, reader.Encoding, reader.Size, CancellationToken.None);
    return ((long)reader.Index.ScannedTo, (long)reader.Index.LineCount - 1);
}, x => Require(x == (fixture.Bytes, fixture.Lines), "Invalid log index output."));

LogCase[] searches =
[
    new("literal-miss", "MARKER_ABSENT", false, [], fixture.Bytes, true),
    new("literal-late-hit", "ОШИБКА_КОНЦА", false, [fixture.Last], fixture.Bytes, true),
    new("literal-dense-first32", "INFO", false, fixture.Dense, fixture.DenseEnd, false),
    new("regex-literal-miss", @"^\d+.*MARKER_ABSENT", true, [], fixture.Bytes, true),
    new("regex-no-literal-miss", @"\d{12}$", true, [], fixture.Bytes, true),
    new("regex-no-literal-late-hit", @"\d{10}$", true, [fixture.Last], fixture.Bytes, true),
];
foreach (var scenario in searches)
{
    Measure(scenario.Name, () => SearchRust(logPath, scenario), () =>
    {
        var page = LogSearch.SearchPage(logPath, null, new LogSearchQuery(scenario.Pattern, scenario.Regex, true, false),
            0, 32, LogSearchLimits.Default with { MaxScannedBytes = (ulong)fixture.Bytes }, CancellationToken.None);
        return new SearchOutput(page.Hits.Select(h => (long)h.LineStart).ToArray(), (long)page.NextOffset, page.Complete);
    }, result => Require(result.Complete == scenario.Complete && result.End == scenario.End && result.Offsets.SequenceEqual(scenario.Offsets),
        $"{scenario.Name}: got {result.Offsets.Length} hits, end={result.End}, complete={result.Complete}. Expected {scenario.Offsets.Length} hits, end={scenario.End}, complete={scenario.Complete}."));
}

int records = Math.Max(128, targetBytes / 4096);
string json = "[" + string.Join(',', Enumerable.Repeat("{\"level\":\"Внимание\",\"count\":3,\"ok\":true}", records)) + "]";
int jsonBytes = Encoding.UTF8.GetByteCount(json);
string nl = Environment.NewLine;
string formattedRecord = $"  {{{nl}    \"level\": \"Внимание\",{nl}    \"count\": 3,{nl}    \"ok\": true{nl}  }}";
string expectedFormattedJson = $"[{nl}{string.Join($",{nl}", Enumerable.Repeat(formattedRecord, records))}{nl}]";
Measure("json-format-string", () => TextOperations.FormatJson(json, "data.json"), () =>
{
    Require(JsonFormat.TryFormat(json, nl, out string formatted, out string error), error);
    return formatted;
}, output => Require(string.Equals(output, expectedFormattedJson, StringComparison.Ordinal), "Invalid formatted JSON output."));

const string editorLine = "id=12345 status=WARNING abcdefghijklmnopqrstuvwxyz 1234567890\n";
const string query = "WARNING";
const string replacement = "OK";
string editorText = string.Concat(Enumerable.Repeat(editorLine, records));
byte[] editorUtf8 = Encoding.UTF8.GetBytes(editorText);
int[] offsets = Enumerable.Range(0, records).Select(i => i * editorLine.Length + editorLine.IndexOf(query, StringComparison.Ordinal)).ToArray();
string expectedReplacement = editorText.Replace(query, replacement, StringComparison.Ordinal);
var rustQuery = new SearchOptions(query, false, true, false);
var netQuery = new SearchQuery(query, false, true, false, false);
var source = new[] { new SearchSource(1, editorText) };
Measure("editor-find-all", () =>
{
    var result = TextOperations.Find(editorText, CreateRustMatcher(rustQuery), records, CancellationToken.None);
    Require(!result.TimedOut && result.Count == records, "Incomplete RustText editor search.");
    return result.Hits.Select(h => (h.Offset, h.Length)).ToArray();
}, () =>
{
    var result = EditorSearch.FindAllRanges(editorUtf8, netQuery, records, TimeSpan.FromMilliseconds(400));
    Require(!result.TimedOut && !result.Truncated && result.TotalMatches == records, "Incomplete NetText editor search.");
    return result.Ranges.Select(h => (h.Start, h.End - h.Start)).ToArray();
}, result => Require(result.SequenceEqual(offsets.Select(offset => (offset, query.Length))), "Invalid editor search ranges."));

Measure("editor-replace-all-string", () =>
{
    var result = TextOperations.ReplaceAll(editorText, CreateRustMatcher(rustQuery), replacement, false);
    return (result.Text, result.Count);
}, () =>
{
    Require(EditorSearch.TryRun(netQuery, SearchCommand.ReplaceAll, source, new NetSearchHit(1, new ByteRange(0, 0)),
        false, replacement, out var result, out var error), error);
    return result is SearchOutcome.Replaced replaced ? (replaced.Text, replaced.Count) : ("", 0);
}, result => Require(result == (expectedReplacement, records), "Invalid replaced document."));

Console.WriteLine($"fixture\tjson\t{jsonBytes}\t{Fingerprint(json)}");
Console.WriteLine($"fixture\teditor\t{editorUtf8.Length}\t{Fingerprint(editorText)}");

void Measure<T>(string name, Func<T> rust, Func<T> net, Action<T> check)
{
    check(rust());
    check(net());
    for (int i = 0; i < iterations; i++)
    {
        bool rustFirst = i % 2 == 0;
        Sample(rustFirst ? "RustText" : "NetText", rustFirst ? rust : net, 1);
        Sample(rustFirst ? "NetText" : "RustText", rustFirst ? net : rust, 2);

        void Sample(string app, Func<T> operation, int order)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            T result = operation();
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            check(result);
            Console.WriteLine($"sample\t{name}\t{app}\t{i + 1}\t{order}\t{ms:F6}\t{allocated}");
        }
    }
}

#if RUSTTEXT_TEXT_MATCHER
static TextMatcher CreateRustMatcher(SearchOptions options) => new(options);
#else
static System.Text.RegularExpressions.Regex CreateRustMatcher(SearchOptions options) => TextOperations.BuildRegex(options);
#endif

static SearchOutput SearchRust(string path, LogCase scenario)
{
    using var file = LogDataReader.Open(path);
    var reader = new LogDataReader(file, TextEncodingKind.Utf8);
    var options = new SearchOptions(scenario.Pattern, scenario.Regex, true, false);
#if RUSTTEXT_BUFFERED_LOG_SEARCH
    var result = reader.Search(options, new LogSearchCursor(0, 0), file.Length, 1, 32, long.MaxValue, CancellationToken.None);
    Require(result.Message is null, result.Message ?? "RustText log search failed.");
    return new(result.Hits.Select(hit => hit.Offset).ToArray(), result.NextCursor?.Offset ?? file.Length, !result.HasMore);
#else
    var regex = TextOperations.BuildRegex(options);
#if RUSTTEXT_SEQUENTIAL_SCAN
    using var scan = reader.BeginSequentialScan();
#endif
    var offsets = new List<long>();
    var pending = new StringBuilder();
    long start = 0;
    long cursor = 0;
    while (cursor < file.Length)
    {
#if RUSTTEXT_SEQUENTIAL_SCAN
        var page = scan.ReadPage(cursor);
#else
        var page = reader.ReadPage(cursor);
#endif
        Require(page.NextOffset > cursor, "RustText log search made no progress.");
        foreach (var row in page.Rows)
        {
            if (!row.StartsWithinLine)
            {
                start = row.StartOffset;
                pending.Clear();
            }
            if (row.IsTruncated)
            {
                pending.Append(row.Text);
                continue;
            }
            string text = pending.Length == 0 ? row.Text : pending.Append(row.Text).ToString();
            if (regex.IsMatch(text))
            {
                offsets.Add(start);
                if (offsets.Count == 32) return new(offsets.ToArray(), row.EndOffset, row.EndOffset == file.Length);
            }
            pending.Clear();
        }
        cursor = page.NextOffset;
    }
    return new(offsets.ToArray(), cursor, cursor == file.Length);
#endif
}

static LogFixture CreateLog(string path, int size)
{
    using var file = File.Create(path);
    var dense = new long[32];
    long denseEnd = 0;
    long lines = 0;
    long length = 0;
    while (length < size)
    {
        if (lines < dense.Length) dense[lines] = length;
        string body = lines % 67 == 0 ? new string('x', 4096) : "payload=operation_ready";
        byte[] bytes = Encoding.UTF8.GetBytes($"{lines:D8} INFO worker=Север {body} flag=ok\r\n");
        file.Write(bytes);
        length += bytes.Length;
        lines++;
        if (lines == dense.Length) denseEnd = length;
    }
    long last = length;
    byte[] tail = Encoding.UTF8.GetBytes("99999999 ERROR ОШИБКА_КОНЦА 1234567890\r\n");
    file.Write(tail);
    return new(length + tail.Length, lines + 1, last, dense, denseEnd);
}

static string Fingerprint(string content) =>
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

record LogFixture(long Bytes, long Lines, long Last, long[] Dense, long DenseEnd);
record LogCase(string Name, string Pattern, bool Regex, long[] Offsets, long End, bool Complete);
record SearchOutput(long[] Offsets, long End, bool Complete);
