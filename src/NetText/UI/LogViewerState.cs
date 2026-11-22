using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using NetText.Core;

namespace NetText.UI;

internal enum LogDestinationKind
{
    None = 0,
    Start = 1,
    Previous = 2,
    Next = 3,
    End = 4,
    Offset = 5,
    Line = 6,
    Match = 7,
    Append = 8,
}

internal readonly record struct LogDestination(LogDestinationKind Kind, ulong Value = 0, bool Follow = false)
{
    public static LogDestination Start => new(LogDestinationKind.Start);
    public static LogDestination Previous => new(LogDestinationKind.Previous);
    public static LogDestination Next => new(LogDestinationKind.Next);
    public static LogDestination End => new(LogDestinationKind.End);
    public static LogDestination AtOffset(ulong offset) => new(LogDestinationKind.Offset, offset);
    public static LogDestination AtLine(ulong offset) => new(LogDestinationKind.Line, offset);
    public static LogDestination AtMatch(ulong offset) => new(LogDestinationKind.Match, offset);
    public static LogDestination Append(bool follow) => new(LogDestinationKind.Append, 0, follow);
}

internal sealed class LogViewerState : IDisposable
{
    private const ulong IndexBatchBytes = 64UL * 1024 * 1024;
    private const ulong SearchBatchBytes = 8UL * 1024 * 1024;
    private const int SearchPageSize = 32;
    private const int SearchPreviewBytes = 300;

    public string FilePath { get; }
    public bool Windows1251 { get; }
    public TextEncoding? EncodingOverride => Windows1251 ? TextEncoding.Windows1251 : null;
    public TextEncoding Encoding { get; private set; }
    public LogBookmark Bookmark { get; }
    public LineIndex Index { get; } = new();
    public LogPage? Page { get; private set; }
    public LogObservation? Observation { get; private set; }

    public bool Reading { get; private set; }
    public bool PreparingTrim { get; private set; }
    public (ulong Done, ulong Total)? PrepProgress { get; private set; }
    public (ulong Done, ulong Total)? TrimProgress { get; private set; }
    public (ulong Scanned, ulong Total)? IndexProgress { get; private set; }
    public bool IndexPaused { get; private set; }
    public string? Notice { get; set; }

    public bool SearchOpen { get; set; }
    public string SearchPattern { get; set; } = "";
    public bool SearchRegex { get; set; }
    public bool SearchCase { get; set; }
    public bool SearchWholeWord { get; set; }
    public ulong SearchOffset { get; private set; }
    public List<LogSearchHit> SearchHits { get; } = [];
    public System.Text.RegularExpressions.Regex? HighlightRegex { get; private set; }
    public string SearchStatus { get; private set; } = "Введите текст или шаблон поиска.";
    public bool SearchComplete { get; private set; }
    private ulong? _pendingSearchSize;
    public bool CanSearchMore => !SearchComplete &&
        (_pendingSearchSize is null || Bookmark.Size > _pendingSearchSize.Value);
    public (ulong Done, ulong Total)? SearchProgress { get; private set; }
    public bool SearchStopping { get; private set; }

    public string JumpPercentInput { get; set; } = "";
    public string TrimPercentInput { get; set; } = "50";

    private readonly Action<Action> _dispatchUi;
    private readonly Action _onChanged;
    private readonly Action<LogPage, ulong?, bool> _applyViewport;
    private readonly Func<int> _getViewportHeightPx;
    private readonly LogChunkCache _chunkCache = new();
    private static readonly ConditionalWeakTable<LogPage, LogLevel[]> FilteredRowLevels = new();

    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _trimCts;
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _pageEditCts;
    private long? _searchCancelTicks;
    private long _loadGeneration;
    private long _filterGeneration;
    private int _observeGeneration;
    private bool _disposed;

    private readonly Stack<FilterCursor> _filterHistory = new();
    private FilterCursor _filterCurrent;
    private FilterCursor? _filterNext;

    public bool EditsPagesByDefault { get; set; }
    public bool IsDraft { get; set; }
    public bool IsConsole { get; set; }
    public bool ConsoleDisconnected { get; private set; }
    public string? ConsoleDisconnectMessage { get; private set; }
    public bool IndexOptIn => Bookmark.IndexOptIn == true;
    public bool NeedsIndexOptIn => !IndexOptIn && Bookmark.Size >= (ulong)LogLevels.LargeLogIndexOptInBytes && Index.ScannedTo < Bookmark.Size;

    public LogLevel SelectedLevels { get; private set; } = LogLevel.All;
    public bool IsFiltered => SelectedLevels != LogLevel.All;
    public FilterPage? FilteredPage { get; private set; }
    public int FilteredPageNumber => _filterHistory.Count + 1;
    public bool CanFilterPrevious => _filterHistory.Count > 0;
    public bool CanFilterNext => _filterNext is not null;
    public string? FilterStatus { get; private set; }

    public PageEditSnapshot? PageEditSnapshot { get; private set; }
    public bool IsEditingPage => PageEditSnapshot is not null;
    public bool HasPendingEdit { get; set; }
    public bool IsSavingPage { get; private set; }
    public bool PageNavigationBlocked => EditsPagesByDefault ? (HasPendingEdit || IsSavingPage) : (IsEditingPage || IsSavingPage);

    public LogViewerState(
        string filePath,
        bool windows1251,
        LogBookmark? restoredBookmark,
        Action<Action> dispatchUi,
        Action onChanged,
        Action<LogPage, ulong?, bool> applyViewport,
        Func<int> getViewportHeightPx,
        Func<TrimPlan, bool> confirmTrim)
    {
        FilePath = filePath;
        Windows1251 = windows1251;
        Encoding = windows1251 ? TextEncoding.Windows1251 : TextEncoding.Utf8;
        Bookmark = restoredBookmark ?? new LogBookmark();
        EditsPagesByDefault = Bookmark.EditPages;
        if (EditsPagesByDefault)
        {
            Bookmark.AutoRefresh = false;
            Bookmark.FollowEnd = false;
        }
        _dispatchUi = dispatchUi;
        _onChanged = onChanged;
        _applyViewport = applyViewport;
        _getViewportHeightPx = getViewportHeightPx;
        _confirmTrim = confirmTrim;
    }

    private readonly Func<TrimPlan, bool> _confirmTrim;

    public bool IsWritingFile => TrimProgress is not null || IsSavingPage;
    public bool IsEnabled => !Reading && !PreparingTrim && !IsWritingFile && !PageNavigationBlocked;
    public ulong PageEndOffset => Page is { Rows.Count: > 0 } ? Page.Rows[^1].End : Bookmark.Offset;
    public ulong? PageLine { get; private set; }
    public ulong? SelectedSearchOffset { get; private set; }
    private long _searchGeneration;
    private bool _updatingPageLine;

    internal static bool TryGetFilteredLevels(LogPage page, out LogLevel[]? levels) =>
        FilteredRowLevels.TryGetValue(page, out levels);

    public PageOptions CurrentPageOptions()
    {
        int vpPx = _getViewportHeightPx();
        int rows = (int)Math.Clamp(Math.Ceiling(vpPx / 21.0) * 3, 16.0, 160.0);
        return new PageOptions(rows, 2048);
    }

    public void StartInitialLoad(bool restoredFromSession)
    {
        var dest = restoredFromSession
            ? (Bookmark.FollowEnd && Bookmark.AutoRefresh
                ? LogDestination.End
                : LogDestination.AtOffset(Math.Max(Bookmark.Offset, Bookmark.Hidden)))
            : LogDestination.Start;
        Navigate(dest, isInitialRestore: restoredFromSession);
    }

    public void Navigate(LogDestination destination, bool isInitialRestore = false)
    {
        if (PreparingTrim || IsWritingFile || PageNavigationBlocked)
        {
            return;
        }

        if (IsFiltered)
        {
            if (destination.Kind == LogDestinationKind.Previous && _filterHistory.TryPop(out var previousCursor))
            {
                LoadFilteredPage(previousCursor);
            }
            else if (destination.Kind == LogDestinationKind.Next && _filterNext is { } nextCursor)
            {
                _filterHistory.Push(_filterCurrent);
                LoadFilteredPage(nextCursor);
            }
            return;
        }

        if ((destination.Kind == LogDestinationKind.Next && Page?.Eof == true) ||
            (destination.Kind == LogDestinationKind.Previous && Bookmark.Offset <= Bookmark.Hidden))
        {
            return;
        }

        long gen = ++_loadGeneration;
        Reading = true;
        var options = CurrentPageOptions();
        var prevPage = Page;
        var prevObs = Observation;
        ulong hidden = Bookmark.Hidden;
        ulong curOffset = Bookmark.Offset;
        var encOverride = EncodingOverride;
        var restoring = isInitialRestore ? Bookmark.Copy() : null;

        _onChanged();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                using var reader = LogReader.Open(FilePath, encOverride, _chunkCache);
                var (obs, _) = LogFile.ObserveHandle(FilePath, reader.Handle, prevObs);
                var enc = reader.Encoding;
                ulong size = reader.Size;

                ulong effectiveHidden = hidden <= size ? hidden : 0;
                bool resetHidden = hidden > size;

                var selectedDestination = destination;
                if (restoring is not null && !restoring.CanRestore(reader.Handle, obs))
                {
                    effectiveHidden = 0;
                    resetHidden = true;
                    selectedDestination = LogDestination.Start;
                }

                LogPage page;
                ulong newOffset;
                switch (selectedDestination.Kind)
                {
                    case LogDestinationKind.Start:
                        page = reader.RenderPage(effectiveHidden, options);
                        newOffset = effectiveHidden;
                        break;

                    case LogDestinationKind.Previous:
                    {
                        ulong target = Math.Max(effectiveHidden, curOffset);
                        if (target <= effectiveHidden)
                        {
                            page = reader.RenderPage(effectiveHidden, options);
                        }
                        else
                        {
                            page = reader.PageBefore(target, options);
                            if (page.Rows.Count > 0 && page.Rows[0].Start < effectiveHidden)
                            {
                                page = reader.RenderPage(effectiveHidden, options);
                            }
                        }
                        newOffset = page.Rows.Count > 0 ? page.Rows[0].Start : effectiveHidden;
                        break;
                    }

                    case LogDestinationKind.Next:
                    {
                        ulong nextOff = prevPage?.NextOffset ?? effectiveHidden;
                        if (nextOff < effectiveHidden) nextOff = effectiveHidden;
                        page = reader.RenderPage(nextOff, options);
                        newOffset = page.Rows.Count > 0 ? page.Rows[0].Start : nextOff;
                        break;
                    }

                    case LogDestinationKind.End:
                        page = reader.PageBefore(size, options);
                        if (page.Rows.Count > 0 && page.Rows[0].Start < effectiveHidden)
                        {
                            page = reader.RenderPage(effectiveHidden, options);
                        }
                        newOffset = page.Rows.Count > 0 ? page.Rows[0].Start : effectiveHidden;
                        break;

                    case LogDestinationKind.Offset:
                    {
                        ulong target = Math.Max(effectiveHidden, Math.Min(destination.Value, size));
                        page = RenderAtAnchoredLine(reader, enc, target, size, options);
                        if (page.Rows.Count > 0 && page.Rows[0].Start < effectiveHidden)
                        {
                            page = reader.RenderPage(effectiveHidden, options);
                        }
                        newOffset = page.Rows.Count > 0 ? page.Rows[0].Start : effectiveHidden;
                        break;
                    }

                    case LogDestinationKind.Line:
                    {
                        ulong target = Math.Max(effectiveHidden, Math.Min(destination.Value, size));
                        page = RenderAtAnchoredLine(reader, enc, target, size, options);
                        newOffset = page.Rows.Count > 0 ? page.Rows[0].Start : effectiveHidden;
                        break;
                    }

                    case LogDestinationKind.Match:
                    {
                        ulong target = Math.Max(effectiveHidden, Math.Min(destination.Value, size));
                        page = reader.RenderPage(target, options);
                        newOffset = page.Rows.Count > 0 ? page.Rows[0].Start : target;
                        break;
                    }

                    case LogDestinationKind.Append:
                        (page, newOffset) = AppendPage(reader, prevPage, options, effectiveHidden, destination.Follow);
                        break;

                    default:
                        page = reader.RenderPage(effectiveHidden, options);
                        newOffset = effectiveHidden;
                        break;
                }

                ulong? lineNumber = LineNumber(reader, newOffset);
                bool scrollToBottom = selectedDestination.Kind is LogDestinationKind.Previous or LogDestinationKind.End
                    || (selectedDestination.Kind == LogDestinationKind.Append && selectedDestination.Follow);
                _dispatchUi(() =>
                {
                    if (_disposed || gen != _loadGeneration) return;
                    Reading = false;
                    Encoding = enc;
                    Observation = obs;
                    Bookmark.Record(obs);
                    Bookmark.Hidden = effectiveHidden;
                    Bookmark.Offset = newOffset;
                    if (resetHidden)
                    {
                        Notice = "Лог изменился. Позиция и скрытая граница сброшены.";
                    }
                    Page = page;
                    PageLine = lineNumber;
                    if (destination.Kind == LogDestinationKind.Match) SelectedSearchOffset = newOffset;
                    _applyViewport(page, lineNumber, scrollToBottom);
                    if (!IndexPaused && IndexProgress is null && Index.ScannedTo < size &&
                        (size < (ulong)LogLevels.LargeLogIndexOptInBytes || IndexOptIn))
                    {
                        StartBackgroundIndexing();
                    }
                    _onChanged();
                });
            }
            catch (Exception ex)
            {
                _dispatchUi(() =>
                {
                    if (_disposed || gen != _loadGeneration) return;
                    Reading = false;
                    Notice = ex is FileNotFoundException
                        ? "Файл лога отсутствует. Ожидание повторного создания."
                        : $"Файл лога временно недоступен. {ex.Message}";
                    _onChanged();
                });
            }
        });
    }

    private static (LogPage Page, ulong Offset) AppendPage(
        LogReader reader,
        LogPage? previous,
        PageOptions options,
        ulong hidden,
        bool follow)
    {
        if (previous is null || previous.Rows.Count == 0)
        {
            var fresh = follow
                ? reader.PageBefore(reader.Size, options)
                : reader.RenderPage(hidden, options);
            if (fresh.Rows.Count > 0 && fresh.Rows[0].Start < hidden)
            {
                fresh = reader.RenderPage(hidden, options);
            }
            ulong off = fresh.Rows.Count > 0 ? fresh.Rows[0].Start : hidden;
            return (fresh, off);
        }

        var rows = new List<LogRow>(previous.Rows);
        ulong readFrom = previous.NextOffset;
        if (!rows[^1].Terminated)
        {
            readFrom = rows[^1].Start;
            rows.RemoveAt(rows.Count - 1);
        }

        int slots = follow ? options.MaxRows : Math.Max(0, options.MaxRows - rows.Count);
        if (slots == 0)
        {
            var kept = new LogPage(rows, previous.NextOffset, reader.Size, previous.NextOffset >= reader.Size);
            return (kept, rows[0].Start);
        }

        var freshSlice = reader.RenderPage(readFrom, options with { MaxRows = slots });
        if (follow && !freshSlice.Eof)
        {
            var tail = reader.PageBefore(reader.Size, options);
            if (tail.Rows.Count > 0 && tail.Rows[0].Start < hidden)
            {
                tail = reader.RenderPage(hidden, options);
            }
            ulong tailOff = tail.Rows.Count > 0 ? tail.Rows[0].Start : hidden;
            return (tail, tailOff);
        }

        rows.AddRange(freshSlice.Rows);
        if (follow && rows.Count > options.MaxRows)
        {
            rows.RemoveRange(0, rows.Count - options.MaxRows);
        }

        ulong offset = rows.Count > 0 ? rows[0].Start : hidden;
        return (new LogPage(rows, freshSlice.NextOffset, reader.Size, freshSlice.Eof), offset);
    }

    public void MonitorTick500Ms()
    {
        if (_disposed || !Bookmark.AutoRefresh || ConsoleDisconnected || EditsPagesByDefault || IsEditingPage ||
            Reading || PreparingTrim || IsWritingFile)
        {
            return;
        }

        int gen = Interlocked.Increment(ref _observeGeneration);
        var prevObs = Observation;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var (obs, change) = LogFile.Observe(FilePath, prevObs);
                _dispatchUi(() =>
                {
                    if (_disposed || gen != _observeGeneration || Reading || PreparingTrim || IsWritingFile)
                    {
                        return;
                    }
                    ApplyObservation(obs, change);
                });
            }
            catch (FileNotFoundException)
            {
                _dispatchUi(() =>
                {
                    if (_disposed || gen != _observeGeneration) return;
                    Notice = "Файл лога отсутствует. Ожидание повторного создания.";
                    _onChanged();
                });
            }
            catch (Exception ex)
            {
                _dispatchUi(() =>
                {
                    if (_disposed || gen != _observeGeneration) return;
                    Notice = $"Файл лога временно недоступен. {ex.Message}";
                    _onChanged();
                });
            }
        });
    }

    private void ApplyObservation(LogObservation obs, LogChangeKind change)
    {
        ulong prevLen = Observation?.Stamp.Length ?? Bookmark.Size;
        Observation = obs;
        switch (change)
        {
            case LogChangeKind.Same:
                return;

            case LogChangeKind.Appended:
            {
                _chunkCache.InvalidateFrom(prevLen);
                Bookmark.Record(obs);
                if (_pendingSearchSize is { } waited && Bookmark.Size > waited)
                    SearchStatus = "Лог дописан. Нажмите «Ещё», чтобы повторить поиск в последней строке.";
                if (!IsFiltered)
                {
                    if (Bookmark.FollowEnd || (Page?.Eof ?? false))
                    {
                        Navigate(LogDestination.Append(Bookmark.FollowEnd));
                    }
                    else
                    {
                        _onChanged();
                    }
                }
                else
                {
                    _onChanged();
                }
                if (!IndexPaused && IndexProgress is null &&
                    (obs.Stamp.Length < (ulong)LogLevels.LargeLogIndexOptInBytes || IndexOptIn))
                {
                    StartBackgroundIndexing();
                }
                break;
            }

            case LogChangeKind.Truncated:
            case LogChangeKind.Rewritten:
            case LogChangeKind.Replaced:
                _chunkCache.Clear();
                ResetIndex();
                ResetSearch();
                Bookmark.Hidden = 0;
                Bookmark.Offset = 0;
                Notice = change switch
                {
                    LogChangeKind.Truncated => "Лог очищен или сокращён. Позиция просмотра сброшена.",
                    LogChangeKind.Rewritten => "Содержимое лога изменилось. Позиция просмотра сброшена.",
                    _ => "Файл лога появился или заменён. Позиция просмотра сброшена.",
                };
                if (IsFiltered)
                {
                    FilterStatus = "Файл изменился. Закройте фильтр и повторите попытку.";
                    _onChanged();
                }
                else
                {
                    Navigate(Bookmark.FollowEnd ? LogDestination.End : LogDestination.Start);
                }
                break;
        }
    }

    public void StartBackgroundIndexing()
    {
        if (IndexPaused || IndexProgress is not null || IsWritingFile)
        {
            return;
        }

        _indexCts?.Cancel();
        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;
        var encOverride = EncodingOverride;
        var expectedIdentity = Observation?.Stamp.Identity;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                using var reader = LogReader.Open(FilePath, encOverride, fileOptions: FileOptions.SequentialScan);
                if (expectedIdentity is not null)
                {
                    var stamp = LogFile.StampOf(FilePath, reader.Handle);
                    if (!expectedIdentity.Equals(stamp.Identity))
                    {
                        _dispatchUi(() =>
                        {
                            if (_disposed) return;
                            IndexProgress = null;
                            _onChanged();
                        });
                        return;
                    }
                }
                ulong total = reader.Size;
                long lastUiTicks = Stopwatch.GetTimestamp();
                while (!ct.IsCancellationRequested)
                {
                    ulong target;
                    lock (Index)
                    {
                        if (Index.ScannedTo >= total) break;
                        target = Math.Min(total, Index.ScannedTo + IndexBatchBytes);
                        ulong scanned = Index.Advance(reader.Handle, reader.Encoding, target, ct);
                        if (scanned == 0) break;
                    }
                    ulong cur = Index.ScannedTo;
                    if (cur >= total || Stopwatch.GetElapsedTime(lastUiTicks).TotalMilliseconds >= 100)
                    {
                        lastUiTicks = Stopwatch.GetTimestamp();
                        _dispatchUi(() =>
                        {
                            if (_disposed) return;
                            IndexProgress = cur >= total ? null : (cur, total);
                            if (cur >= total) Measurements.RecordIndexReady();
                            UpdatePageLine();
                            _onChanged();
                        });
                    }
                }

                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    IndexProgress = null;
                    if (Index.ScannedTo >= total) Measurements.RecordIndexReady();
                    UpdatePageLine();
                    _onChanged();
                });
            }
            catch (OperationCanceledException)
            {
                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    IndexProgress = null;
                    _onChanged();
                });
            }
            catch
            {
                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    IndexProgress = null;
                    _onChanged();
                });
            }
        });
    }

    public void ToggleIndex()
    {
        if (IndexProgress is not null)
        {
            IndexPaused = true;
            _indexCts?.Cancel();
            IndexProgress = null;
            _onChanged();
        }
        else
        {
            Bookmark.IndexOptIn = true;
            IndexPaused = false;
            StartBackgroundIndexing();
            _onChanged();
        }
    }

    public void StartFullIndexOptIn()
    {
        Bookmark.IndexOptIn = true;
        IndexPaused = false;
        StartBackgroundIndexing();
        _onChanged();
    }

    public void MarkConsoleDisconnected(string? message = null)
    {
        ConsoleDisconnected = true;
        ConsoleDisconnectMessage = message;
        Bookmark.AutoRefresh = false;
        if (!string.IsNullOrEmpty(message))
        {
            Notice = message;
        }
        _onChanged();
    }

    public void SetLevelFilter(LogLevel levels)
    {
        if (IsEditingPage && !EditsPagesByDefault) return;
        if (SelectedLevels == levels) return;

        _filterCts?.Cancel();
        _filterCts?.Dispose();
        _filterCts = null;
        _filterHistory.Clear();
        _filterNext = null;
        SelectedLevels = levels;

        if (levels == LogLevel.All)
        {
            FilteredPage = null;
            FilterStatus = null;
            Reading = false;
            if (Page is not null)
            {
                _applyViewport(Page, PageLine, false);
            }
            _onChanged();
            return;
        }

        Bookmark.FollowEnd = false;
        if (levels == LogLevel.None)
        {
            FilteredPage = new FilterPage([], null);
            FilterStatus = "Выберите уровень";
            var empty = new LogPage([], Bookmark.Hidden, Bookmark.Size, true);
            _applyViewport(empty, null, false);
            _onChanged();
            return;
        }

        LoadFilteredPage(new FilterCursor((long)Bookmark.Hidden));
    }

    private void LoadFilteredPage(FilterCursor cursor)
    {
        if (_disposed || IsFiltered == false || SelectedLevels == LogLevel.None) return;

        _filterCts?.Cancel();
        _filterCts?.Dispose();
        _filterCts = new CancellationTokenSource();
        var ct = _filterCts.Token;
        long gen = ++_filterGeneration;
        _filterCurrent = cursor;
        _filterNext = null;
        Reading = true;
        FilterStatus = "Поиск по логу…";
        _onChanged();

        var levels = SelectedLevels;
        var expectedStamp = Observation?.Stamp;
        var expectedObs = Observation;
        var encOverride = EncodingOverride;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var page = LogLevelFilter.ReadFile(
                    FilePath,
                    cursor,
                    levels,
                    expectedStamp,
                    encOverride,
                    expectedObs,
                    ct);

                var logRows = new List<LogRow>(page.Rows.Count);
                var rowLevels = new List<LogLevel>(page.Rows.Count);
                for (int i = 0; i < page.Rows.Count; i++)
                {
                    var r = page.Rows[i];
                    bool terminated = i + 1 >= page.Rows.Count || !page.Rows[i + 1].Continuation;
                    logRows.Add(new LogRow((ulong)r.StartOffset, (ulong)r.EndOffset, r.Text, r.Continuation, terminated));
                    if (!r.Continuation || rowLevels.Count == 0)
                    {
                        rowLevels.Add(r.Level);
                    }
                }

                ulong nextOffset = page.Next is { } next ? (ulong)next.Offset : Bookmark.Size;
                var synthetic = new LogPage(logRows, nextOffset, Bookmark.Size, page.Next is null);
                FilteredRowLevels.Add(synthetic, [.. rowLevels]);

                _dispatchUi(() =>
                {
                    if (_disposed || gen != _filterGeneration || SelectedLevels != levels) return;
                    Reading = false;
                    FilteredPage = page;
                    _filterNext = page.Next;
                    FilterStatus = page.Rows.Count == 0
                        ? "Совпадений нет"
                        : $"Страница {LineNumbers.Format(_filterHistory.Count + 1)}";
                    _applyViewport(synthetic, null, false);
                    _onChanged();
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _dispatchUi(() =>
                {
                    if (_disposed || gen != _filterGeneration) return;
                    Reading = false;
                    _onChanged();
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dispatchUi(() =>
                {
                    if (_disposed || gen != _filterGeneration) return;
                    Reading = false;
                    FilterStatus = $"Не удалось прочитать лог. {ex.Message}";
                    Notice = ex.Message;
                    _onChanged();
                });
            }
        });
    }

    public void BeginPageEdit(Action<PageEditSnapshot>? onReady = null, Action<string>? onError = null)
    {
        if (_disposed || IsConsole || IsFiltered || IsSavingPage || PreparingTrim || IsWritingFile || Reading || Page is not { } page)
        {
            return;
        }

        _pageEditCts?.Cancel();
        _pageEditCts?.Dispose();
        _pageEditCts = new CancellationTokenSource();
        var ct = _pageEditCts.Token;
        Reading = true;
        _onChanged();

        var enc = Encoding;
        var expectedObs = Observation;
        ulong from = page.Rows.Count == 0 ? Bookmark.Offset : page.Rows[0].Start;
        ulong to = Math.Max(from, page.NextOffset);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                FileStamp stamp;
                if (expectedObs is not null)
                {
                    stamp = expectedObs.Stamp;
                }
                else
                {
                    using var handle = File.OpenHandle(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    stamp = LogFile.StampOf(FilePath, handle);
                }

                var snapshot = LogIo.ReadPageEdit(FilePath, from, to, enc, stamp, ct);
                _dispatchUi(() =>
                {
                    if (_disposed || ct.IsCancellationRequested) return;
                    Reading = false;
                    Bookmark.FollowEnd = false;
                    PageEditSnapshot = snapshot;
                    HasPendingEdit = false;
                    Notice = "Ctrl+S сохраняет изменения страницы. Перед переходом сохраните или отмените правки.";
                    onReady?.Invoke(snapshot);
                    _onChanged();
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    Reading = false;
                    _onChanged();
                });
            }
            catch (Exception ex)
            {
                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    Reading = false;
                    Notice = $"Не удалось открыть страницу для правок. {ex.Message}";
                    onError?.Invoke(ex.Message);
                    _onChanged();
                });
            }
        });
    }

    public void UpdatePendingPageEdit(string currentText)
    {
        if (PageEditSnapshot is not { } snapshot) return;
        bool dirty = currentText.Length != snapshot.OriginalText.Length || !string.Equals(currentText, snapshot.OriginalText, StringComparison.Ordinal);
        if (dirty != HasPendingEdit)
        {
            HasPendingEdit = dirty;
            _onChanged();
        }
    }

    public void SavePageEdit(string editedText, Action<FileStamp>? onSaved = null, Action<string>? onError = null)
    {
        if (_disposed || PageEditSnapshot is not { } edit || IsSavingPage) return;
        if (string.Equals(editedText, edit.OriginalText, StringComparison.Ordinal))
        {
            if (!EditsPagesByDefault)
            {
                CancelPageEdit();
            }
            else
            {
                HasPendingEdit = false;
                _onChanged();
            }
            return;
        }

        _pageEditCts?.Cancel();
        _pageEditCts?.Dispose();
        _pageEditCts = new CancellationTokenSource();
        var ct = _pageEditCts.Token;
        IsSavingPage = true;
        Notice = "Сохранение страницы…";
        _onChanged();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var updatedStamp = LogIo.SavePageEdit(FilePath, edit, editedText, ct);
                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    IsSavingPage = false;
                    _chunkCache.Clear();
                    ResetIndex();
                    ResetSearch();
                    PageEditSnapshot = null;
                    HasPendingEdit = false;
                    Observation = null;
                    Bookmark.Offset = edit.From;
                    Notice = "Страница сохранена.";
                    onSaved?.Invoke(updatedStamp);
                    Navigate(LogDestination.AtOffset(edit.From));
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    IsSavingPage = false;
                    Notice = "Сохранение страницы отменено.";
                    _onChanged();
                });
            }
            catch (Exception ex)
            {
                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    IsSavingPage = false;
                    Notice = $"Не удалось сохранить страницу. {ex.Message}";
                    onError?.Invoke(ex.Message);
                    _onChanged();
                });
            }
        });
    }

    public void CancelPageEdit()
    {
        if (IsSavingPage)
        {
            _pageEditCts?.Cancel();
            return;
        }
        _pageEditCts?.Cancel();
        PageEditSnapshot = null;
        HasPendingEdit = false;
        Notice = null;
        if (Page is not null)
        {
            _applyViewport(Page, PageLine, false);
        }
        _onChanged();
    }

    public void ResetIndex()
    {
        _indexCts?.Cancel();
        IndexProgress = null;
        lock (Index)
        {
            Index.Reset();
        }
    }

    public void ToggleAutoRefresh()
    {
        if (EditsPagesByDefault || IsEditingPage || ConsoleDisconnected) return;
        Bookmark.AutoRefresh = !Bookmark.AutoRefresh;
        _onChanged();
    }

    public void ToggleFollowEnd()
    {
        if (IsFiltered || EditsPagesByDefault || IsEditingPage) return;
        Bookmark.FollowEnd = !Bookmark.FollowEnd;
        if (Bookmark.FollowEnd && Bookmark.AutoRefresh)
        {
            Navigate(LogDestination.End);
        }
        else
        {
            _onChanged();
        }
    }

    public void JumpPercent()
    {
        if (!IsEnabled || IsFiltered) return;
        if (!double.TryParse(JumpPercentInput.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pct) ||
            pct < 0 || pct > 100)
        {
            Notice = "Введите положение от 0 до 100%.";
            _onChanged();
            return;
        }

        ulong size = Bookmark.Size;
        ulong target = (ulong)(size * (pct / 100.0));
        if (target < Bookmark.Hidden) target = Bookmark.Hidden;
        Notice = null;
        if (pct >= 100.0)
        {
            Navigate(LogDestination.End);
        }
        else
        {
            Navigate(LogDestination.AtOffset(target));
        }
    }

    public void StartSearch(bool more)
    {
        if (!IsEnabled || IsFiltered || SearchProgress is not null || (more && !CanSearchMore)) return;
        if (string.IsNullOrEmpty(SearchPattern))
        {
            SearchStatus = "Введите текст или шаблон поиска.";
            _onChanged();
            return;
        }

        var query = new LogSearchQuery(SearchPattern, SearchRegex, SearchCase, SearchWholeWord);
        try
        {
            HighlightRegex = LogSearch.Compile(query);
        }
        catch (Exception ex)
        {
            HighlightRegex = null;
            SearchStatus = ex.Message;
            _onChanged();
            return;
        }

        if (!more)
        {
            SearchHits.Clear();
            SelectedSearchOffset = null;
            SearchOffset = Bookmark.Hidden;
            SearchComplete = false;
            _pendingSearchSize = null;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        long generation = ++_searchGeneration;
        _searchCancelTicks = null;
        SearchStopping = false;

        ulong startAt = Math.Max(SearchOffset, Bookmark.Hidden);
        ulong totalSize = Math.Max(1, Bookmark.Size);
        var encOverride = EncodingOverride;
        SearchProgress = (startAt, totalSize);
        SearchStatus = $"Поиск… {Pct(startAt, totalSize)}%";
        _onChanged();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var collected = new List<LogSearchHit>();
                ulong cursor = startAt;
                bool complete = false;
                ulong? pendingSize = null;

                while (collected.Count < SearchPageSize)
                {
                    if (ct.IsCancellationRequested) throw new LogCancelledException();

                    var page = LogSearch.SearchPage(
                        FilePath,
                        encOverride,
                        query,
                        cursor,
                        SearchPageSize - collected.Count,
                        new LogSearchLimits(SearchBatchBytes, LogIo.DefaultSearchLineBytes, SearchPreviewBytes),
                        ct);

                    collected.AddRange(page.Hits);
                    cursor = page.NextOffset;
                    complete = page.Complete;
                    if (page.PendingLineStart is not null) pendingSize = page.FileSize;

                    ulong progress = cursor;
                    _dispatchUi(() =>
                    {
                        if (_disposed || generation != _searchGeneration || SearchStopping) return;
                        SearchProgress = (progress, totalSize);
                        SearchStatus = $"Поиск… {Pct(progress, totalSize)}%";
                        _onChanged();
                    });

                    if (complete || pendingSize is not null) break;
                }

                if (collected.Count > 0 && Index.ScannedTo > 0)
                {
                    try
                    {
                        using var reader = LogReader.Open(FilePath, encOverride, _chunkCache);
                        for (int i = 0; i < collected.Count; i++)
                        {
                            var hit = collected[i];
                            collected[i] = hit with { Line = LineNumber(reader, hit.LineStart) };
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }

                _dispatchUi(() =>
                {
                    if (_disposed || generation != _searchGeneration) return;
                    SearchProgress = null;
                    SearchStopping = false;
                    SearchOffset = cursor;
                    SearchComplete = complete;
                    _pendingSearchSize = pendingSize;
                    if (more && SearchHits.Count != 0 && SearchHits[^1].LineStart >= startAt)
                        SearchHits.RemoveAll(hit => hit.LineStart >= startAt);
                    SearchHits.AddRange(collected);
                    SearchStatus = pendingSize is not null
                        ? "Последний символ оборван. Поиск можно продолжить после дописывания файла."
                        : SearchHits.Count == 0 && complete
                        ? "Совпадений больше нет."
                        : complete
                            ? $"Строк с совпадениями: {SearchHits.Count}. Достигнут конец файла."
                            : $"Строк с совпадениями: {SearchHits.Count}. Нажмите «Ещё» для продолжения.";
                    _onChanged();
                });
            }
            catch (Exception ex) when (ex is LogCancelledException or OperationCanceledException)
            {
                _dispatchUi(() =>
                {
                    if (_disposed || generation != _searchGeneration) return;
                    if (_searchCancelTicks is long t)
                    {
                        Measurements.RecordCancelSearch(t);
                        _searchCancelTicks = null;
                    }
                    SearchProgress = null;
                    SearchStopping = false;
                    SearchStatus = "Поиск отменён.";
                    _onChanged();
                });
            }
            catch (Exception ex)
            {
                _dispatchUi(() =>
                {
                    if (_disposed || generation != _searchGeneration) return;
                    SearchProgress = null;
                    SearchStopping = false;
                    SearchStatus = $"Поиск остановлен. {ex.Message}";
                    _onChanged();
                });
            }
        });
    }

    public void CancelSearch()
    {
        if (SearchProgress is null) return;
        _searchCancelTicks = Stopwatch.GetTimestamp();
        SearchStopping = true;
        SearchStatus = "Остановка поиска…";
        _searchCts?.Cancel();
        _onChanged();
    }

    public void ResetSearch()
    {
        _searchGeneration++;
        _searchCts?.Cancel();
        SearchProgress = null;
        SearchStopping = false;
        SearchHits.Clear();
        SelectedSearchOffset = null;
        HighlightRegex = null;
        SearchOffset = Bookmark.Hidden;
        SearchComplete = false;
        _pendingSearchSize = null;
        SearchStatus = string.IsNullOrEmpty(SearchPattern) ? "Введите текст или шаблон поиска." : "Готово к поиску.";
    }

    public void StartHideStart()
    {
        if (!IsEnabled || IsFiltered) return;
        if (ParseTrimPercent() is not byte pct)
        {
            Notice = "Введите целый процент от 1 до 100.";
            _onChanged();
            return;
        }

        _trimCts?.Cancel();
        _trimCts = new CancellationTokenSource();
        var ct = _trimCts.Token;
        PreparingTrim = true;
        PrepProgress = (0, Math.Max(1, Bookmark.Size));
        _onChanged();

        var enc = Encoding;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var plan = LogTrim.HiddenStartWithProgress(FilePath, pct, enc, ct, (done, total) =>
                {
                    _dispatchUi(() =>
                    {
                        if (_disposed) return;
                        PrepProgress = (done, Math.Max(1, total));
                        _onChanged();
                    });
                });

                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    PreparingTrim = false;
                    PrepProgress = null;
                    Bookmark.Hidden = plan.BoundaryOffset;
                    Notice = $"Скрыто {plan.RemovableBytes} байт от начала.";
                    ResetSearch();
                    Navigate(LogDestination.Start);
                });
            }
            catch (Exception ex)
            {
                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    PreparingTrim = false;
                    PrepProgress = null;
                    Notice = $"Не удалось скрыть начало. {ex.Message}";
                    _onChanged();
                });
            }
        });
    }

    public void StartPhysicalTrim()
    {
        if (!IsEnabled || IsFiltered || IsConsole || IsDraft || EditsPagesByDefault) return;
        if (ParseTrimPercent() is not byte pct || pct == 0)
        {
            Notice = "Укажите процент удаления больше нуля.";
            _onChanged();
            return;
        }

        ResetIndex();
        ResetSearch();
        PreparingTrim = true;
        _trimCts?.Cancel();
        _trimCts = new CancellationTokenSource();
        var ct = _trimCts.Token;
        PrepProgress = (0, Math.Max(1, Bookmark.Size));
        _onChanged();

        var enc = Encoding;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Thread.Sleep(45);
                var plan = LogTrim.PrepareWithProgress(FilePath, pct, enc, ct, (done, total) =>
                {
                    _dispatchUi(() =>
                    {
                        if (_disposed) return;
                        PrepProgress = (done, Math.Max(1, total));
                        _onChanged();
                    });
                });

                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    PreparingTrim = false;
                    PrepProgress = null;
                    if (plan.RemovableBytes == 0)
                    {
                        Notice = "Удаляемый участок пуст.";
                        _onChanged();
                        return;
                    }

                    bool confirmed = _confirmTrim(plan);
                    if (!confirmed)
                    {
                        _onChanged();
                        return;
                    }

                    RunTrimExecution(plan);
                });
            }
            catch (Exception ex)
            {
                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    PreparingTrim = false;
                    PrepProgress = null;
                    Notice = ex is IOException
                        ? $"Нужен эксклюзивный доступ к логу. {ex.Message}"
                        : $"Обрезка не завершена. {ex.Message}";
                    _onChanged();
                });
            }
        });
    }

    private void RunTrimExecution(TrimPlan plan)
    {
        _trimCts?.Cancel();
        _trimCts = new CancellationTokenSource();
        var ct = _trimCts.Token;
        TrimProgress = (0, Math.Max(1, plan.OriginalSize));
        _onChanged();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var outcome = LogTrim.Trim(FilePath, plan, ct, (done, total) =>
                {
                    _dispatchUi(() =>
                    {
                        if (_disposed) return;
                        TrimProgress = (done, Math.Max(1, total));
                        _onChanged();
                    });
                });

                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    TrimProgress = null;
                    Bookmark.Hidden = 0;
                    Bookmark.Offset = 0;
                    Observation = null;
                    Notice = $"Удалено {outcome.RemovedBytes} байт, осталось {outcome.RemainingBytes}. Копия: {outcome.BackupPath}";
                    Navigate(LogDestination.Start);
                });
            }
            catch (Exception ex)
            {
                _dispatchUi(() =>
                {
                    if (_disposed) return;
                    TrimProgress = null;
                    Notice = $"Обрезка не завершена. {ex.Message}";
                    _onChanged();
                });
            }
        });
    }

    public void RevealAll()
    {
        if (!IsEnabled || IsFiltered || Bookmark.Hidden == 0) return;
        Bookmark.Hidden = 0;
        ResetSearch();
        Navigate(LogDestination.Start);
    }

    public void CancelTrim()
    {
        _trimCts?.Cancel();
    }

    public string LeftStatusText()
    {
        if (IsSavingPage)
            return "Сохранение страницы…";
        if (TrimProgress is { Done: var td, Total: var tt })
            return $"Обрезка… {Pct(td, tt)}%. Отмена доступна до изменения исходника.";
        if (PrepProgress is { Done: var pd, Total: var pt })
            return $"Поиск границы… {Pct(pd, pt)}%";
        if (PreparingTrim)
            return "Проверка границы…";
        if (Reading)
            return "Чтение лога…";
        if (IsFiltered && FilterStatus is not null)
            return FilterStatus;
        if (Notice is not null)
            return Notice;
        if (IndexProgress is { Scanned: var isc, Total: var itot })
            return $"Индексирование… {Pct(isc, itot)}% · Посчитано {LineNumbers.Format((long)Index.LineCount)}";
        if (Index.ScannedTo > 0)
            return $"Посчитано · {LineNumbers.Format((long)Index.LineCount)}";
        if (NeedsIndexOptIn)
            return "Нет индекса строк";
        return "Просмотр лога";
    }

    private byte? ParseTrimPercent()
    {
        if (!byte.TryParse(TrimPercentInput.Trim(), out byte pct) || pct is < 1 or > 100)
        {
            return null;
        }
        return pct;
    }

    private static int Pct(ulong done, ulong total) =>
        total == 0 ? 0 : (int)Math.Clamp((done * 100.0) / total, 0, 100);

    private LogPage RenderAtAnchoredLine(LogReader reader, TextEncoding encoding, ulong target, ulong size, PageOptions options)
    {
        ulong aligned = TextFormat.AlignOffset(Math.Min(target, size), encoding);
        SparseLine anchor;
        lock (Index)
        {
            anchor = Index.FindAnchor(aligned);
        }

        if (aligned - anchor.Offset <= LogIo.MaxBackwardScan)
        {
            ulong lineStart = LineIndex.LineStartAtFromAnchor(reader.Handle, encoding, aligned, size, anchor);
            return reader.RenderPage(lineStart, options);
        }

        return reader.RenderPage(reader.NearbyLineStart(aligned) ?? aligned, options);
    }

    private ulong? LineNumber(LogReader reader, ulong offset)
    {
        if (offset == 0) return 1;
        SparseLine anchor;
        lock (Index)
        {
            if (offset > Index.ScannedTo) return null;
            anchor = Index.FindAnchor(offset);
        }
        if (offset - anchor.Offset > LogIo.MaxBackwardScan) return null;
        return LineIndex.LineAtFromAnchor(reader.Handle, reader.Encoding, offset, reader.Size, anchor).Line;
    }

    private void UpdatePageLine()
    {
        if (_disposed || _updatingPageLine || PageLine is not null || Page is not { Rows.Count: > 0 } page ||
            page.Rows[0].Start > Index.ScannedTo) return;
        _updatingPageLine = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            ulong? number = null;
            try
            {
                using var reader = LogReader.Open(FilePath, EncodingOverride, _chunkCache);
                number = LineNumber(reader, page.Rows[0].Start);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _dispatchUi(() =>
            {
                _updatingPageLine = false;
                if (_disposed || !ReferenceEquals(Page, page) || number is null) return;
                PageLine = number;
                _onChanged();
            });
        });
    }

    public void SeekTime()
    {
        if (!IsEnabled || IsFiltered || SearchProgress is not null) return;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        long generation = ++_searchGeneration;
        var query = SearchPattern;
        ulong hidden = Bookmark.Hidden;
        ulong offset = Bookmark.Offset;
        SearchProgress = (hidden, Math.Max(1, Bookmark.Size));
        SearchStatus = "Поиск по времени…";
        _onChanged();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            ulong? result = null;
            string? error = null;
            try
            {
                using var reader = LogReader.Open(FilePath, EncodingOverride, _chunkCache);
                result = LogTimestamp.Seek(reader, hidden, offset, query, token);
            }
            catch (OperationCanceledException) { error = "Поиск отменён."; }
            catch (Exception failure) { error = failure.Message; }
            _dispatchUi(() =>
            {
                if (_disposed || generation != _searchGeneration) return;
                SearchProgress = null;
                SearchStopping = false;
                SearchStatus = error ?? (result is null ? "Записи с таким или более поздним временем нет." : "Переход к записи.");
                if (result is { } found) Navigate(LogDestination.AtMatch(found));
                _onChanged();
            });
        });
    }

    internal static string AssemblePageText(List<LogRow> rows)
    {
        if (rows.Count == 0) return "";
        var sb = new StringBuilder(rows.Count * 128);
        for (int i = 0; i < rows.Count; i++)
        {
            sb.Append(rows[i].Text);
            if (rows[i].Terminated && i + 1 < rows.Count)
            {
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    internal static byte[] JoinRowsUtf8(List<LogRow> rows) =>
        rows.Count == 0 ? [] : System.Text.Encoding.UTF8.GetBytes(AssemblePageText(rows));

    public void Dispose()
    {
        _disposed = true;
        _indexCts?.Cancel();
        _searchCts?.Cancel();
        _trimCts?.Cancel();
        _filterCts?.Cancel();
        _pageEditCts?.Cancel();
        _indexCts?.Dispose();
        _searchCts?.Dispose();
        _trimCts?.Dispose();
        _filterCts?.Dispose();
        _pageEditCts?.Dispose();
    }
}
