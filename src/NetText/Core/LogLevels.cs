using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace NetText.Core;

[Flags]
internal enum LogLevel
{
    None = 0,
    Other = 1,
    Trace = 2,
    Debug = 4,
    Info = 8,
    Warning = 16,
    Error = 32,
    Fatal = 64,
    All = Other | Trace | Debug | Info | Warning | Error | Fatal,
}

internal static partial class LogLevels
{
    public const long LargeFilePromptBytes = 100L * 1024 * 1024;
    public const long LargeLogIndexOptInBytes = 128L * 1024 * 1024;

    [GeneratedRegex("""(?:^|[\s{,|])["']?(?:@l|level|loglevel|log_level|severity|severityText|log\.level)["']?\s*[:=]\s*["']?(?<level>[A-Za-z]+)\b""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StructuredLevel();

    [GeneratedRegex("""(?:^\s*(?:(?:\d{4}[-/.]\d\d[-/.]\d\d[T ]+)?\d\d:\d\d:\d\d[\d.,Z:+-]*\s+)?(?:\[[^\]\r\n]{1,64}\]\s*){0,4}|\[\s*(?:\d\d:\d\d:\d\d[\d.,]*\s+)?|\|\s*)(?<level>trc|trce|trace|vrb|verbose|dbg|dbug|debug|inf|info|information|notice|wrn|warn|warning|err|error|fail|ftl|fatal|crit|critical|panic)(?=$|[\s\]|):,;-])""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderLevel();

    [GeneratedRegex(@"^\s*\[?\d{4}[-/.]\d\d[-/.]\d\d[T ]\d\d:\d\d:\d\d\b", RegexOptions.CultureInvariant)]
    private static partial Regex DatedHeader();

    public static bool IsLogPath(string? path) =>
        string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase);

    public static bool HasLogRecords(string path, TextEncoding? encoding = null, CancellationToken token = default)
    {
        const int sampleBytes = 16 * 1024;
        using var reader = LogReader.Open(path, encoding, fileOptions: FileOptions.SequentialScan);
        ulong limit = Math.Min(reader.Size, (ulong)sampleBytes);
        var sample = reader.RenderPageUntil(0, limit, new PageOptions(32, sampleBytes));

        int nonEmpty = 0;
        int recognized = 0;
        bool prevContinues = false;

        foreach (var row in sample.Rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.Text.Contains('\0')) return false;
            bool startsWithinLine = prevContinues;
            prevContinues = row.Continues;
            if (startsWithinLine || string.IsNullOrWhiteSpace(row.Text)) continue;

            nonEmpty++;
            string text = row.Text.TrimStart();
            LogLevel level = Classify(text);
            if (level != LogLevel.Other)
            {
                int prefixEnd = text[0] switch
                {
                    '[' => text.IndexOf(']', 0, Math.Min(32, text.Length)),
                    '<' => text.IndexOf('>', 0, Math.Min(5, text.Length)),
                    _ => -1,
                };
                if (prefixEnd > 0 && Classify(text[..(prefixEnd + 1)]) != LogLevel.Other)
                {
                    return true;
                }
            }
            if (level != LogLevel.Other || DatedHeader().IsMatch(text))
            {
                recognized++;
            }
        }

        return recognized >= 2 && recognized * 2 >= nonEmpty;
    }

    public static LogLevel Classify(string? text)
    {
        if (string.IsNullOrEmpty(text)) return LogLevel.Other;

        int close = text[0] == '<' ? text.IndexOf('>', 1, Math.Min(4, text.Length - 1)) : -1;
        if (close is >= 2 and <= 4 &&
            uint.TryParse(text.AsSpan(1, close - 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint priority) &&
            priority <= 191)
        {
            return (priority % 8) switch
            {
                <= 2 => LogLevel.Fatal,
                3 => LogLevel.Error,
                4 => LogLevel.Warning,
                5 or 6 => LogLevel.Info,
                _ => LogLevel.Debug,
            };
        }

        int end = Math.Min(1024, text.Length);
        var field = StructuredLevel().Match(text, 0, end);
        if (!field.Success)
        {
            if (text.AsSpan().TrimStart().StartsWith("{")) return LogLevel.Other;
            field = HeaderLevel().Match(text, 0, Math.Min(end, 256));
        }

        if (!field.Success) return LogLevel.Other;

        return field.Groups["level"].Value.ToUpperInvariant() switch
        {
            "TRC" or "TRCE" or "TRACE" or "VRB" or "VERBOSE" => LogLevel.Trace,
            "DBG" or "DBUG" or "DEBUG" => LogLevel.Debug,
            "INF" or "INFO" or "INFORMATION" or "NOTICE" => LogLevel.Info,
            "WRN" or "WARN" or "WARNING" => LogLevel.Warning,
            "ERR" or "ERROR" or "FAIL" => LogLevel.Error,
            "FTL" or "FATAL" or "CRIT" or "CRITICAL" or "PANIC" => LogLevel.Fatal,
            _ => LogLevel.Other,
        };
    }
}
