using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace NetText.Core;

public sealed record SearchQuery(
    string Text,
    bool Regex,
    bool CaseSensitive,
    bool WholeWord,
    bool AllTabs);

public readonly record struct ByteRange(int Start, int End)
{
    public int Length => Math.Max(0, End - Start);
}

public readonly record struct SearchHit(ulong Tab, ByteRange Range);

public sealed record SearchSource(ulong Tab, string Text);

public sealed record SearchUtf8Source(ulong Tab, byte[] Buffer, int Length);

public enum SearchCommand
{
    None = 0,
    Next = 1,
    Previous = 2,
    Replace = 3,
    ReplaceAll = 4,
    List = 5,
}

public abstract record SearchOutcome
{
    public sealed record Found(SearchHit? Hit, int Total, IReadOnlyList<SearchHit>? Results = null, bool Partial = false) : SearchOutcome;
    public sealed record Replaced(ByteRange Range, string Text, int Count) : SearchOutcome;
    public sealed record ReplacedUtf8(ByteRange Range, byte[] Buffer, int Length, bool FromPool, int Count) : SearchOutcome;
    public sealed record NoReplacements : SearchOutcome;
    internal sealed record Navigated(SearchHit? Hit, SearchNavigationCursor? Cursor) : SearchOutcome;
}

internal sealed record SearchNavigationCursor(int Segment, int Character, int Byte, SearchHit? LastHit);

public sealed record FindAllResult(
    IReadOnlyList<ByteRange> Ranges,
    int TotalMatches,
    bool Truncated,
    bool TimedOut,
    string? Error = null);

public static partial class EditorSearch
{
    private const int ResultLimit = 100;
    internal const int MaxDocumentBytes = 64 * 1024 * 1024;
    public const long AutomaticSearchMaxBytes = 1024L * 1024 * 1024;
    public const int MaxMultiCaretMatches = 10_000;
    public const int FindAllTimeoutMs = 400;
    private static readonly long SearchBudget = Stopwatch.Frequency * 2 / 5;

    public static FindAllResult FindAllRanges(ReadOnlySpan<byte> utf8, SearchQuery query) =>
        FindAllRanges(utf8, query, MaxMultiCaretMatches, TimeSpan.FromMilliseconds(FindAllTimeoutMs));

    public static FindAllResult FindAllRanges(ReadOnlySpan<byte> utf8, SearchQuery query, int maxMatches, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(query.Text))
        {
            return new FindAllResult([], 0, false, false, "Введите текст или регулярное выражение.");
        }
        if (Encoding.UTF8.GetByteCount(query.Text) > 4096)
        {
            return new FindAllResult([], 0, false, false, "Запрос поиска превышает 4 КиБ.");
        }

        int limit = Math.Max(0, maxMatches);
        var ranges = new List<ByteRange>(Math.Min(limit, 256));
        long started = Stopwatch.GetTimestamp();

        if (!query.Regex && query.CaseSensitive && !query.WholeWord)
        {
            int maxNeedle = Encoding.UTF8.GetMaxByteCount(query.Text.Length);
            byte[]? rentedNeedle = maxNeedle > 512 ? ArrayPool<byte>.Shared.Rent(maxNeedle) : null;
            Span<byte> needleScratch = rentedNeedle is not null ? rentedNeedle.AsSpan(0, maxNeedle) : stackalloc byte[512];
            try
            {
                int needleLen = Encoding.UTF8.GetBytes(query.Text, needleScratch);
                ReadOnlySpan<byte> needle = needleScratch[..needleLen];
                int total = 0;
                bool truncated = false;
                int pos = 0;
                while (pos <= utf8.Length - needle.Length)
                {
                    int rel = utf8[pos..].IndexOf(needle);
                    if (rel < 0) break;
                    int matchStart = pos + rel;
                    int matchEnd = matchStart + needle.Length;
                    pos = matchEnd == matchStart ? matchEnd + 1 : matchEnd;

                    total++;
                    if (ranges.Count < limit)
                    {
                        ranges.Add(new ByteRange(matchStart, matchEnd));
                    }
                    else
                    {
                        truncated = true;
                    }

                    if ((total & 1023) == 0 && Stopwatch.GetElapsedTime(started) >= timeout)
                    {
                        return new FindAllResult(ranges, total, truncated, true);
                    }
                }
                return new FindAllResult(ranges, total, truncated, false);
            }
            finally
            {
                if (rentedNeedle is not null) ArrayPool<byte>.Shared.Return(rentedNeedle);
            }
        }

        if (!TryCompile(query, out var regex, out string error))
        {
            return new FindAllResult([], 0, false, false, error);
        }

        int maxChars = Encoding.UTF8.GetMaxCharCount(utf8.Length);
        char[] chars = ArrayPool<char>.Shared.Rent(Math.Max(1, maxChars));
        int totalRegex = 0;
        bool truncatedRegex = false;
        try
        {
            int charCount = Encoding.UTF8.GetChars(utf8, chars);
            ReadOnlySpan<char> span = chars.AsSpan(0, charCount);
            int charCursor = 0;
            int byteCursor = 0;

            foreach (ValueMatch m in regex!.EnumerateMatches(span))
            {
                byteCursor += Encoding.UTF8.GetByteCount(span.Slice(charCursor, m.Index - charCursor));
                int matchBytes = Encoding.UTF8.GetByteCount(span.Slice(m.Index, m.Length));
                int byteStart = byteCursor;
                int byteEnd = byteCursor + matchBytes;
                charCursor = m.Index + m.Length;
                byteCursor = byteEnd;

                totalRegex++;
                if (ranges.Count < limit)
                {
                    ranges.Add(new ByteRange(byteStart, byteEnd));
                }
                else
                {
                    truncatedRegex = true;
                }

                if ((totalRegex & 1023) == 0 && Stopwatch.GetElapsedTime(started) >= timeout)
                {
                    return new FindAllResult(ranges, totalRegex, truncatedRegex, true);
                }
            }

            return new FindAllResult(ranges, totalRegex, truncatedRegex, false);
        }
        catch (RegexMatchTimeoutException)
        {
            return new FindAllResult(ranges, totalRegex, truncatedRegex, true);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    public static bool TryRun(
        SearchQuery query,
        SearchCommand command,
        IReadOnlyList<SearchSource> sources,
        SearchHit origin,
        bool skipCurrent,
        string replacement,
        out SearchOutcome outcome,
        out string error)
    {
        if (!TryValidateQuery(query, out error))
        {
            outcome = new SearchOutcome.NoReplacements();
            return false;
        }

        if (!query.Regex && !query.WholeWord && query.CaseSensitive &&
            command is SearchCommand.Replace or SearchCommand.ReplaceAll)
        {
            try
            {
                outcome = RunLiteralReplace(query.Text, command, sources, origin, replacement);
                return true;
            }
            catch (TimeoutException)
            {
                outcome = new SearchOutcome.NoReplacements();
                error = "Превышен лимит времени замены.";
                return false;
            }
        }

        if (!TryCompile(query, out var regex, out error))
        {
            outcome = new SearchOutcome.NoReplacements();
            return false;
        }

        if (command is SearchCommand.Replace or SearchCommand.ReplaceAll)
        {
            try
            {
                outcome = RunReplace(query, regex!, command, sources, origin, replacement);
                return true;
            }
            catch (TimeoutException)
            {
                outcome = new SearchOutcome.NoReplacements();
                error = "Превышен лимит времени замены.";
                return false;
            }
        }

        outcome = RunNavigate(regex!, command, sources, origin, skipCurrent);
        return true;
    }

    public static bool TryRunUtf8(
        SearchQuery query,
        SearchCommand command,
        IReadOnlyList<SearchUtf8Source> sources,
        SearchHit origin,
        bool skipCurrent,
        string replacement,
        out SearchOutcome outcome,
        out string error)
    {
        try
        {
            return TryRunUtf8Core(query, command, sources, origin, skipCurrent, replacement, out outcome, out error);
        }
        catch (TimeoutException)
        {
            outcome = new SearchOutcome.NoReplacements();
            error = "Превышен лимит времени поиска.";
            return false;
        }
        catch (ArgumentException ex)
        {
            outcome = new SearchOutcome.NoReplacements();
            error = $"Ошибка замены. {ex.Message}";
            return false;
        }
    }

    private static bool TryRunUtf8Core(
        SearchQuery query, SearchCommand command, IReadOnlyList<SearchUtf8Source> sources,
        SearchHit origin, bool skipCurrent, string replacement, out SearchOutcome outcome, out string error)
    {
        if (Encoding.UTF8.GetByteCount(query.Text) > 4096)
        {
            outcome = new SearchOutcome.NoReplacements();
            error = "Запрос поиска превышает 4 КиБ.";
            return false;
        }
        if (!query.Regex && query.CaseSensitive && !query.WholeWord)
        {
            if (string.IsNullOrEmpty(query.Text))
            {
                outcome = new SearchOutcome.NoReplacements();
                error = "Введите текст или регулярное выражение.";
                return false;
            }

            error = "";
            outcome = command is SearchCommand.Replace or SearchCommand.ReplaceAll
                ? RunUtf8LiteralReplace(query.Text, command, sources, origin, replacement)
                : RunUtf8LiteralNavigate(query.Text, command, sources, origin, skipCurrent);
            return true;
        }

        if (!TryCompile(query, out var regex, out error))
        {
            outcome = new SearchOutcome.NoReplacements();
            return false;
        }

        outcome = command is SearchCommand.Replace or SearchCommand.ReplaceAll
            ? RunUtf8RegexReplace(query, regex!, command, sources, origin, replacement)
            : RunUtf8RegexNavigate(regex!, command, sources, origin, skipCurrent);
        return true;
    }

    private static bool TryValidateQuery(SearchQuery query, out string error)
    {
        if (string.IsNullOrEmpty(query.Text))
        {
            error = "Введите текст или регулярное выражение.";
            return false;
        }
        if (Encoding.UTF8.GetByteCount(query.Text) > 4096)
        {
            error = "Запрос поиска превышает 4 КиБ.";
            return false;
        }
        error = "";
        return true;
    }

    public static bool TryCompile(SearchQuery query, out Regex? regex, out string error)
    {
        if (!TryValidateQuery(query, out error))
        {
            regex = null;
            return false;
        }

        var pattern = query.Regex ? query.Text : Regex.Escape(query.Text);
        if (query.WholeWord)
        {
            pattern = $@"\b(?:{pattern})\b";
        }

        var options = RegexOptions.CultureInvariant;
        if (!query.CaseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        try
        {
            regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(400));
            error = "";
            return true;
        }
        catch (ArgumentException ex)
        {
            regex = null;
            error = $"Ошибка регулярного выражения: {ex.Message}";
            return false;
        }
    }

    private static SearchOutcome RunNavigate(
        Regex regex,
        SearchCommand command,
        IReadOnlyList<SearchSource> sources,
        SearchHit origin,
        bool skipCurrent)
    {
        int originTabIndex = -1;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].Tab == origin.Tab)
            {
                originTabIndex = i;
                break;
            }
        }

        SearchHit? first = null;
        SearchHit? last = null;
        SearchHit? nextHit = null;
        SearchHit? prevHit = null;
        int total = 0;

        for (int tabIndex = 0; tabIndex < sources.Count; tabIndex++)
        {
            var source = sources[tabIndex];
            ReadOnlySpan<char> span = source.Text.AsSpan();
            int charCursor = 0;
            int byteCursor = 0;

            foreach (ValueMatch m in regex.EnumerateMatches(span))
            {
                byteCursor += Encoding.UTF8.GetByteCount(span.Slice(charCursor, m.Index - charCursor));
                int matchBytes = Encoding.UTF8.GetByteCount(span.Slice(m.Index, m.Length));
                int byteStart = byteCursor;
                int byteEnd = byteCursor + matchBytes;
                charCursor = m.Index + m.Length;
                byteCursor = byteEnd;

                var hit = new SearchHit(source.Tab, new ByteRange(byteStart, byteEnd));
                total++;
                first ??= hit;
                last = hit;

                if (originTabIndex < 0)
                {
                    nextHit ??= hit;
                    prevHit = hit;
                    continue;
                }

                bool atOrAfterOrigin = tabIndex > originTabIndex ||
                    (tabIndex == originTabIndex && hit.Range.Start >= origin.Range.Start);
                if (atOrAfterOrigin && !(skipCurrent && hit == origin))
                {
                    nextHit ??= hit;
                }

                bool beforeOrigin = tabIndex < originTabIndex ||
                    (tabIndex == originTabIndex && hit.Range.Start < origin.Range.Start);
                if (beforeOrigin)
                {
                    prevHit = hit;
                }
            }
        }

        var chosen = command == SearchCommand.Previous
            ? prevHit ?? last
            : nextHit ?? first;
        return new SearchOutcome.Found(chosen, total);
    }

    private static SearchOutcome RunReplace(
        SearchQuery query,
        Regex regex,
        SearchCommand command,
        IReadOnlyList<SearchSource> sources,
        SearchHit origin,
        string replacement)
    {
        SearchSource? active = null;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].Tab == origin.Tab)
            {
                active = sources[i];
                break;
            }
        }

        if (active is null)
        {
            return new SearchOutcome.NoReplacements();
        }

        var text = active.Text;
        ReadOnlySpan<char> span = text.AsSpan();
        long deadline = Stopwatch.GetTimestamp() + SearchBudget;

        if (command == SearchCommand.ReplaceAll)
        {
            if (!query.Regex)
            {
                int count = 0;
                var sb = new StringBuilder(text.Length);
                int lastChar = 0;
                foreach (ValueMatch m in regex.EnumerateMatches(span))
                {
                    sb.Append(span.Slice(lastChar, m.Index - lastChar));
                    sb.Append(replacement);
                    lastChar = m.Index + m.Length;
                    count++;
                    CheckSearchDeadline(deadline, count);
                }
                if (count == 0) return new SearchOutcome.NoReplacements();
                sb.Append(span[lastChar..]);
                return new SearchOutcome.Replaced(new ByteRange(0, Encoding.UTF8.GetByteCount(span)), sb.ToString(), count);
            }

            var rsb = new StringBuilder(text.Length);
            int rLastChar = 0;
            int replaced = 0;
            for (var m = regex.Match(text); m.Success; m = m.NextMatch())
            {
                rsb.Append(text, rLastChar, m.Index - rLastChar);
                rsb.Append(m.Result(replacement));
                rLastChar = m.Index + m.Length;
                CheckSearchDeadline(deadline, ++replaced);
            }
            if (replaced == 0) return new SearchOutcome.NoReplacements();
            rsb.Append(text, rLastChar, text.Length - rLastChar);
            return new SearchOutcome.Replaced(new ByteRange(0, Encoding.UTF8.GetByteCount(span)), rsb.ToString(), replaced);
        }

        if (!query.Regex)
        {
            int charCursor = 0;
            int byteCursor = 0;
            ByteRange? firstRange = null;
            ByteRange? chosenRange = null;
            foreach (ValueMatch m in regex.EnumerateMatches(span))
            {
                byteCursor += Encoding.UTF8.GetByteCount(span.Slice(charCursor, m.Index - charCursor));
                int matchBytes = Encoding.UTF8.GetByteCount(span.Slice(m.Index, m.Length));
                var r = new ByteRange(byteCursor, byteCursor + matchBytes);
                charCursor = m.Index + m.Length;
                byteCursor += matchBytes;

                firstRange ??= r;
                if (r.Start >= origin.Range.Start)
                {
                    chosenRange = r;
                    break;
                }
            }
            chosenRange ??= firstRange;
            return chosenRange is { } picked
                ? new SearchOutcome.Replaced(picked, replacement, 1)
                : new SearchOutcome.NoReplacements();
        }

        var regexMatches = regex.Matches(text);
        if (regexMatches.Count == 0)
        {
            return new SearchOutcome.NoReplacements();
        }

        Match? target = null;
        int curChar = 0;
        int curByte = 0;
        ByteRange firstMatchRange = default;
        ByteRange targetRange = default;
        for (int i = 0; i < regexMatches.Count; i++)
        {
            var m = regexMatches[i];
            curByte += Encoding.UTF8.GetByteCount(span.Slice(curChar, m.Index - curChar));
            int matchBytes = Encoding.UTF8.GetByteCount(span.Slice(m.Index, m.Length));
            var r = new ByteRange(curByte, curByte + matchBytes);
            curChar = m.Index + m.Length;
            curByte += matchBytes;

            if (i == 0) firstMatchRange = r;
            if (r.Start >= origin.Range.Start)
            {
                target = m;
                targetRange = r;
                break;
            }
        }
        if (target is null)
        {
            target = regexMatches[0];
            targetRange = firstMatchRange;
        }

        return new SearchOutcome.Replaced(
            targetRange,
            target.Result(replacement),
            1);
    }

    private static SearchOutcome RunLiteralReplace(
        string needleText,
        SearchCommand command,
        IReadOnlyList<SearchSource> sources,
        SearchHit origin,
        string replacement)
    {
        SearchSource? active = null;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].Tab == origin.Tab)
            {
                active = sources[i];
                break;
            }
        }

        if (active is null)
        {
            return new SearchOutcome.NoReplacements();
        }

        string text = active.Text;
        ReadOnlySpan<char> span = text.AsSpan();
        ReadOnlySpan<char> needle = needleText.AsSpan();
        long deadline = Stopwatch.GetTimestamp() + SearchBudget;

        if (command == SearchCommand.Replace)
        {
            int needleBytes = Encoding.UTF8.GetByteCount(needle);
            int pos = 0;
            int charCursor = 0;
            int byteCursor = 0;
            int scanned = 0;
            ByteRange? firstRange = null;
            ByteRange? chosenRange = null;
            while (pos <= span.Length - needle.Length)
            {
                int rel = span[pos..].IndexOf(needle, StringComparison.Ordinal);
                if (rel < 0) break;
                int matchIndex = pos + rel;
                int matchEnd = matchIndex + needle.Length;
                CheckSearchDeadline(deadline, ++scanned);

                bool splitsSurrogate =
                    (matchIndex > 0 && char.IsHighSurrogate(span[matchIndex - 1]) && char.IsLowSurrogate(span[matchIndex])) ||
                    (matchEnd < span.Length && char.IsHighSurrogate(span[matchEnd - 1]) && char.IsLowSurrogate(span[matchEnd]));
                if (splitsSurrogate)
                {
                    pos = matchIndex + 1;
                    continue;
                }

                byteCursor += Encoding.UTF8.GetByteCount(span.Slice(charCursor, matchIndex - charCursor));
                var r = new ByteRange(byteCursor, byteCursor + needleBytes);
                charCursor = matchEnd;
                byteCursor += needleBytes;
                pos = charCursor;

                firstRange ??= r;
                if (r.Start >= origin.Range.Start)
                {
                    chosenRange = r;
                    break;
                }
            }
            chosenRange ??= firstRange;
            return chosenRange is { } picked
                ? new SearchOutcome.Replaced(picked, replacement, 1)
                : new SearchOutcome.NoReplacements();
        }

        int first = span.IndexOf(needle, StringComparison.Ordinal);
        if (first < 0)
        {
            return new SearchOutcome.NoReplacements();
        }

        const int MaxOffsetBuffer = 16_384;
        int[]? offsets = ArrayPool<int>.Shared.Rent(Math.Clamp(span.Length / 64, 64, MaxOffsetBuffer));
        int count = 0;
        try
        {
            int pos = first;
            while (true)
            {
                if (offsets is not null)
                {
                    if (count == offsets.Length)
                    {
                        if (offsets.Length >= MaxOffsetBuffer)
                        {
                            ArrayPool<int>.Shared.Return(offsets);
                            offsets = null;
                        }
                        else
                        {
                            int nextSize = Math.Min(MaxOffsetBuffer, offsets.Length * 2);
                            int[] grown = ArrayPool<int>.Shared.Rent(nextSize);
                            offsets.AsSpan(0, count).CopyTo(grown);
                            ArrayPool<int>.Shared.Return(offsets);
                            offsets = grown;
                        }
                    }

                    if (offsets is not null)
                    {
                        offsets[count] = pos;
                    }
                }

                count++;
                CheckSearchDeadline(deadline, count);

                int nextStart = pos + needle.Length;
                if (nextStart > span.Length - needle.Length) break;
                int rel = span[nextStart..].IndexOf(needle, StringComparison.Ordinal);
                if (rel < 0) break;
                pos = nextStart + rel;
            }

            long outLenLong = (long)span.Length + (long)count * (replacement.Length - needle.Length);
            if (outLenLong < 0 || outLenLong > int.MaxValue)
            {
                throw new OutOfMemoryException();
            }

            int outLen = (int)outLenLong;
            string replaced;
            if (outLen == 0)
            {
                replaced = "";
            }
            else if (offsets is not null)
            {
                replaced = string.Create(
                    outLen,
                    new LiteralReplaceOffsetState(text, replacement, needle.Length, offsets, count),
                    static (dest, s) =>
                    {
                        ReadOnlySpan<char> src = s.Text.AsSpan();
                        ReadOnlySpan<char> rep = s.Replacement.AsSpan();
                        int[] offs = s.Offsets;
                        int srcPos = 0;
                        int dstPos = 0;
                        for (int i = 0; i < s.Count; i++)
                        {
                            int matchPos = offs[i];
                            int copyLen = matchPos - srcPos;
                            if (copyLen > 0)
                            {
                                src.Slice(srcPos, copyLen).CopyTo(dest.Slice(dstPos));
                                dstPos += copyLen;
                            }
                            if (rep.Length > 0)
                            {
                                rep.CopyTo(dest.Slice(dstPos));
                                dstPos += rep.Length;
                            }
                            srcPos = matchPos + s.NeedleLength;
                        }
                        if (srcPos < src.Length)
                        {
                            src.Slice(srcPos).CopyTo(dest.Slice(dstPos));
                        }
                    });
            }
            else
            {
                replaced = string.Create(
                    outLen,
                    new LiteralReplaceScanState(text, needleText, replacement, first, deadline),
                    static (dest, s) =>
                    {
                        ReadOnlySpan<char> src = s.Text.AsSpan();
                        ReadOnlySpan<char> n = s.Needle.AsSpan();
                        ReadOnlySpan<char> rep = s.Replacement.AsSpan();
                        int srcPos = 0;
                        int dstPos = 0;
                        int pos = s.FirstMatch;
                        int written = 0;
                        while (true)
                        {
                            int copyLen = pos - srcPos;
                            if (copyLen > 0)
                            {
                                src.Slice(srcPos, copyLen).CopyTo(dest.Slice(dstPos));
                                dstPos += copyLen;
                            }
                            if (rep.Length > 0)
                            {
                                rep.CopyTo(dest.Slice(dstPos));
                                dstPos += rep.Length;
                            }
                            srcPos = pos + n.Length;
                            CheckSearchDeadline(s.Deadline, ++written);

                            if (srcPos > src.Length - n.Length) break;
                            int rel = src[srcPos..].IndexOf(n, StringComparison.Ordinal);
                            if (rel < 0) break;
                            pos = srcPos + rel;
                        }
                        if (srcPos < src.Length)
                        {
                            src.Slice(srcPos).CopyTo(dest.Slice(dstPos));
                        }
                    });
            }

            return new SearchOutcome.Replaced(
                new ByteRange(0, Encoding.UTF8.GetByteCount(span)),
                replaced,
                count);
        }
        finally
        {
            if (offsets is not null) ArrayPool<int>.Shared.Return(offsets);
        }
    }

    private readonly struct LiteralReplaceOffsetState(
        string text,
        string replacement,
        int needleLength,
        int[] offsets,
        int count)
    {
        public readonly string Text = text;
        public readonly string Replacement = replacement;
        public readonly int NeedleLength = needleLength;
        public readonly int[] Offsets = offsets;
        public readonly int Count = count;
    }

    private readonly struct LiteralReplaceScanState(
        string text,
        string needle,
        string replacement,
        int firstMatch,
        long deadline)
    {
        public readonly string Text = text;
        public readonly string Needle = needle;
        public readonly string Replacement = replacement;
        public readonly int FirstMatch = firstMatch;
        public readonly long Deadline = deadline;
    }

    private static SearchOutcome RunUtf8LiteralNavigate(
        string needleText,
        SearchCommand command,
        IReadOnlyList<SearchUtf8Source> sources,
        SearchHit origin,
        bool skipCurrent)
    {
        int maxNeedle = Encoding.UTF8.GetMaxByteCount(needleText.Length);
        byte[]? rentedNeedle = maxNeedle > 512 ? ArrayPool<byte>.Shared.Rent(maxNeedle) : null;
        Span<byte> needleScratch = rentedNeedle is not null ? rentedNeedle.AsSpan(0, maxNeedle) : stackalloc byte[512];
        try
        {
            int needleLen = Encoding.UTF8.GetBytes(needleText, needleScratch);
            ReadOnlySpan<byte> needle = needleScratch[..needleLen];

            int originTabIndex = FindOriginTabIndex(sources, origin.Tab);
            SearchHit? first = null, last = null, nextHit = null, prevHit = null;
            int total = 0;
            var results = new List<SearchHit>(ResultLimit);
            long deadline = Stopwatch.GetTimestamp() + SearchBudget;

            for (int tabIndex = 0; tabIndex < sources.Count; tabIndex++)
            {
                var source = sources[tabIndex];
                ReadOnlySpan<byte> span = source.Buffer.AsSpan(0, source.Length);
                int pos = 0;
                while (pos <= span.Length - needle.Length)
                {
                    int rel = span[pos..].IndexOf(needle);
                    if (rel < 0) break;
                    int matchStart = pos + rel;
                    int matchEnd = matchStart + needle.Length;
                    pos = matchEnd;

                    var hit = new SearchHit(source.Tab, new ByteRange(matchStart, matchEnd));
                    total++;
                    if (results.Count < ResultLimit) results.Add(hit);
                    first ??= hit;
                    last = hit;
                    UpdateOriginCandidates(tabIndex, originTabIndex, hit, origin, skipCurrent, ref nextHit, ref prevHit);
                    if ((total & 1023) == 0 && Stopwatch.GetTimestamp() >= deadline)
                        return new SearchOutcome.Found(command == SearchCommand.Previous ? prevHit : nextHit, total, results, true);
                }
            }

            return new SearchOutcome.Found(command == SearchCommand.Previous ? prevHit ?? last : nextHit ?? first, total, results);
        }
        finally
        {
            if (rentedNeedle is not null) ArrayPool<byte>.Shared.Return(rentedNeedle);
        }
    }

    private static SearchOutcome RunUtf8LiteralReplace(
        string needleText,
        SearchCommand command,
        IReadOnlyList<SearchUtf8Source> sources,
        SearchHit origin,
        string replacement)
    {
        SearchUtf8Source? active = FindActiveSource(sources, origin.Tab);
        if (active is null) return new SearchOutcome.NoReplacements();
        long deadline = Stopwatch.GetTimestamp() + SearchBudget;

        ReadOnlySpan<byte> span = active.Buffer.AsSpan(0, active.Length);
        int maxNeedle = Encoding.UTF8.GetMaxByteCount(needleText.Length);
        byte[]? rentedNeedle = maxNeedle > 512 ? ArrayPool<byte>.Shared.Rent(maxNeedle) : null;
        Span<byte> needleScratch = rentedNeedle is not null ? rentedNeedle.AsSpan(0, maxNeedle) : stackalloc byte[512];
        try
        {
            int needleLen = Encoding.UTF8.GetBytes(needleText, needleScratch);
            ReadOnlySpan<byte> needle = needleScratch[..needleLen];

            if (command != SearchCommand.ReplaceAll)
            {
                int pos = 0;
                ByteRange? firstRange = null, chosenRange = null;
                while (pos <= span.Length - needle.Length)
                {
                    int rel = span[pos..].IndexOf(needle);
                    if (rel < 0) break;
                    int matchStart = pos + rel;
                    int matchEnd = matchStart + needle.Length;
                    var r = new ByteRange(matchStart, matchEnd);
                    firstRange ??= r;
                    if (r.Start >= origin.Range.Start)
                    {
                        chosenRange = r;
                        break;
                    }
                    pos = matchEnd;
                }
                chosenRange ??= firstRange;
                return chosenRange is { } picked
                    ? new SearchOutcome.Replaced(picked, replacement, 1)
                    : new SearchOutcome.NoReplacements();
            }

            int count = 0;
            int scan = 0;
            while (scan <= span.Length - needle.Length)
            {
                int rel = span[scan..].IndexOf(needle);
                if (rel < 0) break;
                count++;
                CheckSearchDeadline(deadline, count);
                scan += rel + needle.Length;
            }
            if (count == 0) return new SearchOutcome.NoReplacements();

            int maxRep = Encoding.UTF8.GetMaxByteCount(replacement.Length);
            byte[]? rentedRep = maxRep > 512 ? ArrayPool<byte>.Shared.Rent(maxRep) : null;
            Span<byte> repScratch = rentedRep is not null ? rentedRep.AsSpan(0, maxRep) : stackalloc byte[512];
            try
            {
                int repLen = Encoding.UTF8.GetBytes(replacement, repScratch);
                ReadOnlySpan<byte> rep = repScratch[..repLen];

                long totalLen = (long)span.Length + (long)count * (rep.Length - needle.Length);
                if (totalLen > Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(replacement), "Результат слишком велик.");
                byte[] outBuf = ArrayPool<byte>.Shared.Rent(Math.Max(1, (int)totalLen));
                int outPos = 0;
                int srcPos = 0;
                int written = 0;
                try
                {
                    while (srcPos <= span.Length - needle.Length)
                    {
                        int rel = span[srcPos..].IndexOf(needle);
                        if (rel < 0) break;
                        span.Slice(srcPos, rel).CopyTo(outBuf.AsSpan(outPos));
                        outPos += rel;
                        rep.CopyTo(outBuf.AsSpan(outPos));
                        outPos += rep.Length;
                        srcPos += rel + needle.Length;
                        CheckSearchDeadline(deadline, ++written);
                    }
                    if (srcPos < span.Length)
                    {
                        span[srcPos..].CopyTo(outBuf.AsSpan(outPos));
                        outPos += span.Length - srcPos;
                    }
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(outBuf);
                    throw;
                }

                return new SearchOutcome.ReplacedUtf8(new ByteRange(0, span.Length), outBuf, outPos, FromPool: true, count);
            }
            finally
            {
                if (rentedRep is not null) ArrayPool<byte>.Shared.Return(rentedRep);
            }
        }
        finally
        {
            if (rentedNeedle is not null) ArrayPool<byte>.Shared.Return(rentedNeedle);
        }
    }

    private static SearchOutcome RunUtf8RegexNavigate(
        Regex regex,
        SearchCommand command,
        IReadOnlyList<SearchUtf8Source> sources,
        SearchHit origin,
        bool skipCurrent)
    {
        int originTabIndex = FindOriginTabIndex(sources, origin.Tab);
        SearchHit? first = null, last = null, nextHit = null, prevHit = null;
        int total = 0;
        var results = new List<SearchHit>(ResultLimit);
        long deadline = Stopwatch.GetTimestamp() + SearchBudget;

        for (int tabIndex = 0; tabIndex < sources.Count; tabIndex++)
        {
            var source = sources[tabIndex];
            ReadOnlySpan<byte> utf8 = source.Buffer.AsSpan(0, source.Length);
            int maxChars = Encoding.UTF8.GetMaxCharCount(utf8.Length);
            char[] chars = ArrayPool<char>.Shared.Rent(Math.Max(1, maxChars));
            try
            {
                int charCount = Encoding.UTF8.GetChars(utf8, chars);
                ReadOnlySpan<char> span = chars.AsSpan(0, charCount);
                int charCursor = 0;
                int byteCursor = 0;

                foreach (ValueMatch m in regex.EnumerateMatches(span))
                {
                    byteCursor += Encoding.UTF8.GetByteCount(span.Slice(charCursor, m.Index - charCursor));
                    int matchBytes = Encoding.UTF8.GetByteCount(span.Slice(m.Index, m.Length));
                    int byteStart = byteCursor;
                    int byteEnd = byteCursor + matchBytes;
                    charCursor = m.Index + m.Length;
                    byteCursor = byteEnd;

                    var hit = new SearchHit(source.Tab, new ByteRange(byteStart, byteEnd));
                    total++;
                    if (results.Count < ResultLimit) results.Add(hit);
                    first ??= hit;
                    last = hit;
                    UpdateOriginCandidates(tabIndex, originTabIndex, hit, origin, skipCurrent, ref nextHit, ref prevHit);
                    if ((total & 1023) == 0 && Stopwatch.GetTimestamp() >= deadline)
                        return new SearchOutcome.Found(command == SearchCommand.Previous ? prevHit : nextHit, total, results, true);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return new SearchOutcome.Found(command == SearchCommand.Previous ? prevHit : nextHit, total, results, true);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
            }
        }

        return new SearchOutcome.Found(command == SearchCommand.Previous ? prevHit ?? last : nextHit ?? first, total, results);
    }

    private static SearchOutcome RunUtf8RegexReplace(
        SearchQuery query,
        Regex regex,
        SearchCommand command,
        IReadOnlyList<SearchUtf8Source> sources,
        SearchHit origin,
        string replacement)
    {
        SearchUtf8Source? active = FindActiveSource(sources, origin.Tab);
        if (active is null) return new SearchOutcome.NoReplacements();
        long deadline = Stopwatch.GetTimestamp() + SearchBudget;

        ReadOnlySpan<byte> utf8 = active.Buffer.AsSpan(0, active.Length);
        if (query.Regex && replacement.Contains('$', StringComparison.Ordinal))
        {
            string text = Encoding.UTF8.GetString(utf8);
            return RunReplace(query, regex, command, [new SearchSource(active.Tab, text)], origin, replacement);
        }

        int maxChars = Encoding.UTF8.GetMaxCharCount(utf8.Length);
        char[] chars = ArrayPool<char>.Shared.Rent(Math.Max(1, maxChars));
        try
        {
            int charCount = Encoding.UTF8.GetChars(utf8, chars);
            ReadOnlySpan<char> span = chars.AsSpan(0, charCount);

            if (command != SearchCommand.ReplaceAll)
            {
                int charCursor = 0;
                int byteCursor = 0;
                ByteRange? firstRange = null, chosenRange = null;
                foreach (ValueMatch m in regex.EnumerateMatches(span))
                {
                    byteCursor += Encoding.UTF8.GetByteCount(span.Slice(charCursor, m.Index - charCursor));
                    int matchBytes = Encoding.UTF8.GetByteCount(span.Slice(m.Index, m.Length));
                    var r = new ByteRange(byteCursor, byteCursor + matchBytes);
                    charCursor = m.Index + m.Length;
                    byteCursor += matchBytes;

                    firstRange ??= r;
                    if (r.Start >= origin.Range.Start)
                    {
                        chosenRange = r;
                        break;
                    }
                }
                chosenRange ??= firstRange;
                return chosenRange is { } picked
                    ? new SearchOutcome.Replaced(picked, replacement, 1)
                    : new SearchOutcome.NoReplacements();
            }

            int repBytesCount = Encoding.UTF8.GetByteCount(replacement);
            int estimated = Math.Max(utf8.Length, 16);
            byte[] outBuf = ArrayPool<byte>.Shared.Rent(estimated);
            int outLen = 0;
            int count = 0;
            int lastChar = 0;
            byte[] repBytes = ArrayPool<byte>.Shared.Rent(Math.Max(1, repBytesCount));
            try
            {
                int repActual = Encoding.UTF8.GetBytes(replacement, repBytes);
                foreach (ValueMatch m in regex.EnumerateMatches(span))
                {
                    int chunkLen = Encoding.UTF8.GetByteCount(span.Slice(lastChar, m.Index - lastChar));
                    EnsureCapacity(ref outBuf, outLen + chunkLen + repActual);
                    outLen += Encoding.UTF8.GetBytes(span.Slice(lastChar, m.Index - lastChar), outBuf.AsSpan(outLen));
                    if (repActual > 0)
                    {
                        repBytes.AsSpan(0, repActual).CopyTo(outBuf.AsSpan(outLen));
                        outLen += repActual;
                    }
                    lastChar = m.Index + m.Length;
                    count++;
                    CheckSearchDeadline(deadline, count);
                }

                if (count == 0)
                {
                    ArrayPool<byte>.Shared.Return(outBuf);
                    return new SearchOutcome.NoReplacements();
                }

                int tailLen = Encoding.UTF8.GetByteCount(span[lastChar..]);
                EnsureCapacity(ref outBuf, outLen + tailLen);
                outLen += Encoding.UTF8.GetBytes(span[lastChar..], outBuf.AsSpan(outLen));
                return new SearchOutcome.ReplacedUtf8(new ByteRange(0, utf8.Length), outBuf, outLen, FromPool: true, count);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(outBuf);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(repBytes);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private static void EnsureCapacity(ref byte[] buf, int required)
    {
        if (required <= buf.Length) return;
        int nextSize = Math.Max(required, buf.Length * 2);
        byte[] grown = ArrayPool<byte>.Shared.Rent(nextSize);
        buf.AsSpan().CopyTo(grown);
        ArrayPool<byte>.Shared.Return(buf);
        buf = grown;
    }

    private static void CheckSearchDeadline(long deadline, int matched)
    {
        if ((matched & 1023) == 0 && Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException();
    }

    private static int FindOriginTabIndex(IReadOnlyList<SearchUtf8Source> sources, ulong originTab)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].Tab == originTab) return i;
        }
        return -1;
    }

    private static SearchUtf8Source? FindActiveSource(IReadOnlyList<SearchUtf8Source> sources, ulong tab)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].Tab == tab) return sources[i];
        }
        return null;
    }

    private static void UpdateOriginCandidates(
        int tabIndex,
        int originTabIndex,
        SearchHit hit,
        SearchHit origin,
        bool skipCurrent,
        ref SearchHit? nextHit,
        ref SearchHit? prevHit)
    {
        if (originTabIndex < 0)
        {
            nextHit ??= hit;
            prevHit = hit;
            return;
        }

        bool atOrAfterOrigin = tabIndex > originTabIndex ||
            (tabIndex == originTabIndex && hit.Range.Start >= origin.Range.Start);
        if (atOrAfterOrigin && !(skipCurrent && hit == origin))
        {
            nextHit ??= hit;
        }

        bool beforeOrigin = tabIndex < originTabIndex ||
            (tabIndex == originTabIndex && hit.Range.Start < origin.Range.Start);
        if (beforeOrigin)
        {
            prevHit = hit;
        }
    }
}
