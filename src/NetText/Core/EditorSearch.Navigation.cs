using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace NetText.Core;

public static partial class EditorSearch
{
    private readonly record struct NavigationSegment(int Source, int Lower, int Upper);

    internal static bool TryNavigateUtf8(SearchQuery query, bool backwards, IReadOnlyList<SearchUtf8Source> sources,
        SearchHit origin, bool skipCurrent, SearchNavigationCursor? restore, out SearchOutcome outcome, out string error,
        int matchBatch = int.MaxValue)
    {
        outcome = new SearchOutcome.Navigated(null, null);
        if (!TryCompile(query, out var regex, out error)) return false;
        int active = FindOriginTabIndex(sources, origin.Tab);
        if (active < 0) return true;
        int start = Math.Clamp(origin.Range.Start, 0, sources[active].Length);
        var segments = new List<NavigationSegment>(sources.Count + 1);
        segments.Add(backwards ? new(active, 0, start) : new(active, start, int.MaxValue));
        for (int i = 1; i < sources.Count; i++)
        {
            int index = backwards ? (active + sources.Count - i) % sources.Count : (active + i) % sources.Count;
            segments.Add(new NavigationSegment(index, 0, int.MaxValue));
        }
        segments.Add(backwards ? new(active, start, int.MaxValue) : new(active, 0, start + 1));

        long deadline = Stopwatch.GetTimestamp() + SearchBudget;
        int examined = 0;
        for (int i = restore?.Segment ?? 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var source = sources[segment.Source];
            if (source.Length > MaxDocumentBytes) continue;
            var bytes = source.Buffer.AsSpan(0, source.Length);
            var text = Encoding.UTF8.GetString(bytes);
            var saved = restore?.Segment == i ? restore : null;
            int position = saved?.Character ?? Encoding.UTF8.GetCharCount(bytes[..segment.Lower]);
            int bytePosition = saved?.Byte ?? segment.Lower;
            SearchHit? last = saved?.LastHit;
            try
            {
                while (position <= text.Length)
                {
                    if (++examined > matchBatch || Stopwatch.GetTimestamp() >= deadline)
                    {
                        outcome = Pause();
                        return true;
                    }
                    var match = regex!.Match(text, position);
                    if (!match.Success) break;
                    int first = bytePosition + Encoding.UTF8.GetByteCount(text.AsSpan(position, match.Index - position));
                    if (first >= segment.Upper) break;
                    int end = first + Encoding.UTF8.GetByteCount(match.ValueSpan);
                    var hit = new SearchHit(source.Tab, new ByteRange(first, end));
                    if (i != 0 || !skipCurrent || hit != origin)
                    {
                        if (!backwards)
                        {
                            outcome = new SearchOutcome.Navigated(hit, null);
                            return true;
                        }
                        last = hit;
                    }
                    position = match.Index + match.Length;
                    bytePosition = end;
                    if (match.Length == 0)
                    {
                        if (position == text.Length) break;
                        int width = char.IsSurrogatePair(text, position) ? 2 : 1;
                        bytePosition += Encoding.UTF8.GetByteCount(text.AsSpan(position, width));
                        position += width;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                outcome = Pause();
                return true;
            }
            if (last is not null)
            {
                outcome = new SearchOutcome.Navigated(last, null);
                return true;
            }

            SearchOutcome Pause() => new SearchOutcome.Navigated(null, new SearchNavigationCursor(i, position, bytePosition, last));
        }
        return true;
    }
}
