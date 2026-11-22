using System.Globalization;
using System.Text.RegularExpressions;

namespace NetText.Core;

internal static partial class LogTimestamp
{
    private readonly record struct Stamp(DateOnly Date, long Nanoseconds) : IComparable<Stamp>
    {
        public int CompareTo(Stamp other) => Date == other.Date ? Nanoseconds.CompareTo(other.Nanoseconds) : Date.CompareTo(other.Date);
    }

    private readonly record struct Entry(ulong Start, ulong End, Stamp Time);

    private const string TimePattern = @"(?<h>[0-9]{2}):(?<m>[0-9]{2}):(?<s>[0-9]{2})(?:[.,](?<f>[0-9]{1,9}))?(?![.0-9]|,[0-9])";

    [GeneratedRegex(@"(?<![0-9])(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})[ T]" + TimePattern, RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"\A" + TimePattern + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimeOnlyPattern();

    private static long? ReadTime(Match match)
    {
        if (!match.Success) return null;
        int hour = int.Parse(match.Groups["h"].ValueSpan, CultureInfo.InvariantCulture);
        int minute = int.Parse(match.Groups["m"].ValueSpan, CultureInfo.InvariantCulture);
        int second = int.Parse(match.Groups["s"].ValueSpan, CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59 || second > 60) return null;
        long fraction = 0;
        var span = match.Groups["f"].ValueSpan;
        foreach (char digit in span) fraction = fraction * 10 + digit - '0';
        for (int i = span.Length; i < 9; i++) fraction *= 10;
        return ((hour * 60L + minute) * 60 + second) * 1_000_000_000 + fraction;
    }

    private static Stamp? ReadDate(Match match) =>
        ReadTime(match) is long nanos && DateOnly.TryParseExact(match.Groups["date"].ValueSpan, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? new Stamp(date, nanos) : null;

    internal static ulong? Seek(LogReader reader, ulong minimum, ulong reference, string query, CancellationToken token)
    {
        query = query.Trim();
        var dateMatch = DatePattern().Match(query);
        Stamp target;
        if (dateMatch.Index == 0 && dateMatch.Length == query.Length && ReadDate(dateMatch) is { } date)
            target = date;
        else if (ReadTime(TimeOnlyPattern().Match(query)) is { } time)
        {
            var atReference = ReadEntry(reader.NearbyLineStart(reference, minimum));
            var context = atReference ?? Scan(null);
            if (context is not { } origin) return null;
            target = new Stamp(origin.Time.Date, time);
        }
        else throw new FormatException("Введите дату и время ГГГГ-ММ-ДД ЧЧ:ММ:СС или только ЧЧ:ММ:СС.");

        if (minimum >= reader.Size) return null;
        var firstEntry = ReadEntry(minimum);
        int unit = TextFormat.UnitSize(reader.Encoding);
        ulong lastProbe = reader.Size - Math.Min(reader.Size, (ulong)unit);
        var lastEntry = ReadEntry(reader.NearbyLineStart(lastProbe, minimum));
        if (firstEntry is not { } left || lastEntry is not { } right || left.Time.CompareTo(right.Time) > 0)
            return Scan(target)?.Start;
        if (left.Time.CompareTo(target) >= 0) return left.Start;
        if (right.Time.CompareTo(target) < 0) return null;
        while (left.End < right.Start)
        {
            token.ThrowIfCancellationRequested();
            ulong middle = left.End + (right.Start - left.End) / 2;
            var probe = ReadEntry(reader.NearbyLineStart(middle, minimum));
            if (probe is not { } current || current.Start < left.End || current.End > right.Start ||
                current.Time.CompareTo(left.Time) < 0 || current.Time.CompareTo(right.Time) > 0)
                return Scan(target)?.Start;
            if (current.Time.CompareTo(target) >= 0) right = current;
            else left = current;
        }
        return right.Start;

        Entry? ReadEntry(ulong? offset)
        {
            token.ThrowIfCancellationRequested();
            if (offset is null || offset.Value >= reader.Size) return null;
            var page = reader.RenderPage(offset.Value, new PageOptions(1, LogIo.MaxRowBytes));
            if (page.Rows.Count == 0) return null;
            var row = page.Rows[0];
            if (row.Continues || ReadStamp(row.Text) is not { } stamp) return null;
            return new Entry(row.Start, row.End, stamp);
        }

        Entry? Scan(Stamp? threshold)
        {
            ulong cursor = minimum;
            ulong lineStart = minimum;
            bool continuation = false;
            while (cursor < reader.Size)
            {
                token.ThrowIfCancellationRequested();
                var page = reader.RenderPage(cursor, new PageOptions(256, LogIo.DefaultRowBytes));
                foreach (var row in page.Rows)
                {
                    token.ThrowIfCancellationRequested();
                    if (!continuation) lineStart = row.Start;
                    if (ReadStamp(row.Text) is { } stamp && (threshold is null || stamp.CompareTo(threshold.Value) >= 0))
                        return new Entry(lineStart, row.End, stamp);
                    continuation = row.Continues;
                }
                if (page.NextOffset <= cursor) break;
                cursor = page.NextOffset;
            }
            return null;
        }
    }

    private static Stamp? ReadStamp(string text)
    {
        for (var match = DatePattern().Match(text); match.Success; match = match.NextMatch())
            if (ReadDate(match) is { } stamp) return stamp;
        return null;
    }
}
