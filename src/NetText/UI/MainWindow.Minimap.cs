using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NetText.Core;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private const int MapSamples = 64;
    private const int MinimapPadding = 3;
    private const int MinimapPreviewColumns = 66;
    private const int MinimapRowPitch = 2;
    private const int MapLogicalWidth = MinimapPreviewColumns + MinimapPadding * 2;
    private const int ScrollbarThickness = 11;

    private readonly record struct ScrollBounds(Win32.RECT Map, Win32.RECT Vertical, Win32.RECT Horizontal);
    private readonly record struct ScrollDrag(ulong Id, HitKind Kind, int Pointer, int LastPointer, int Offset, int Travel, ScrollRange Range, int Origin, int Length);

    private sealed class MinimapCacheEntry : IDisposable
    {
        public ulong Revision;
        public uint InkColor;
        public int Rows;
        public int Width;
        public int Height;
        public nint Bitmap;
        public nint Bits;

        public void Dispose()
        {
            if (Bitmap != 0)
            {
                Win32.DeleteObject(Bitmap);
                Bitmap = 0;
                Bits = 0;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [LibraryImport("msimg32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AlphaBlend(
        nint hdcDest,
        int xoriginDest,
        int yoriginDest,
        int wDest,
        int hDest,
        nint hdcSrc,
        int xoriginSrc,
        int yoriginSrc,
        int wSrc,
        int hSrc,
        BLENDFUNCTION ftn);

    private readonly Dictionary<ulong, ScrollBounds> _scrollBounds = [];
    private readonly Dictionary<ulong, (ulong Hidden, ulong Length, byte[] Samples)> _mapSamples = [];
    private readonly Dictionary<ulong, MinimapCacheEntry> _minimapPreviews = [];
    private readonly HashSet<ulong> _mapsLoading = [];
    private ScrollDrag? _scrollDrag;
    private bool _scrollLayoutQueued;
    private bool _forwardingWheel;

    private void PositionDocument(Document doc, Win32.RECT area)
    {
        int width = Math.Max(0, area.Width);
        int height = Math.Max(0, area.Height);
        int vScrollWidth = width >= S(80) && height >= S(26) ? S(ScrollbarThickness) : 0;
        int mapWidth = _settings.Minimap && width >= S(160) && height >= S(44) ? S(MapLogicalWidth) : 0;

        bool nativeVertical = vScrollWidth == 0;
        if (doc.NativeVerticalScroll != nativeVertical)
        {
            doc.NativeVerticalScroll = nativeVertical;
            if (doc.Viewport is { } viewport) viewport.SetVerticalScrollbar(nativeVertical);
            else ScintillaInterop.SetVerticalScrollbar(doc.EditorHwnd, nativeVertical);
        }

        int editorWidth = Math.Max(0, width - mapWidth - vScrollWidth);
        int editorRight = area.Left + editorWidth;
        int mapRight = editorRight + mapWidth;
        int scrollHeight = doc.HorizontalScroll && !_wrap && height >= S(26) ? S(ScrollbarThickness) : 0;
        int editorHeight = Math.Max(0, height - scrollHeight);

        var map = new Win32.RECT { Left = editorRight, Right = mapRight, Top = area.Top, Bottom = area.Bottom };
        var vertical = new Win32.RECT { Left = mapRight, Right = area.Right, Top = area.Top, Bottom = area.Bottom };
        var horizontal = new Win32.RECT { Left = area.Left, Right = editorRight, Top = area.Bottom - scrollHeight, Bottom = area.Bottom };
        _scrollBounds[doc.Id] = new ScrollBounds(map, vertical, horizontal);

        if (doc.EditorHwnd != 0) Win32.MoveWindow(doc.EditorHwnd, area.Left, area.Top, editorWidth, editorHeight, true);
        if (doc.FilterPreviewHwnd != 0)
        {
            Win32.MoveWindow(doc.FilterPreviewHwnd, area.Left, area.Top, editorWidth, editorHeight, true);
        }
        if (_pageEditors.TryGetValue(doc.Id, out nint pageHwnd) && pageHwnd != 0)
        {
            Win32.MoveWindow(pageHwnd, area.Left, area.Top, editorWidth, editorHeight, true);
        }
        SyncDocumentScroll(doc);
        if (mapWidth > 0 && doc.Log is { } log) UpdateLogMinimap(doc.Id, log);
    }

    private ScrollRange VerticalRange(Document doc)
    {
        if (doc.Log is { } log)
        {
            ulong size = log.Bookmark.Size;
            ulong hidden = Math.Min(size, log.Bookmark.Hidden);
            var visible = doc.Viewport?.VisibleRange;
            ulong first = Math.Clamp(visible?.Start ?? log.Bookmark.Offset, hidden, size);
            ulong last = Math.Clamp(visible?.End ?? first, first, size);
            return new(first - hidden, last - hidden, size - hidden);
        }

        nint editor = doc.EditorHwnd;
        return ScrollRange.FromViewport((ulong)Math.Max(0, ScintillaInterop.GetScroll(editor).FirstVisibleLine),
            (ulong)ScintillaInterop.LinesOnScreen(editor), (ulong)ScintillaInterop.DisplayLineCount(editor));
    }

    private ScrollRange HorizontalRange(Document doc)
    {
        if (_wrap) return default;
        if (doc.Viewport is { } viewport) return viewport.HorizontalRange;
        nint editor = doc.EditorHwnd;
        return ScrollRange.FromViewport((ulong)Math.Max(0, ScintillaInterop.GetScroll(editor).XOffset),
            (ulong)ScintillaInterop.TextViewportWidth(editor), (ulong)doc.MeasuredWidth);
    }

    private void MeasureEditorWidth(Document doc)
    {
        if (_wrap || !_scrollBounds.ContainsKey(doc.Id)) return;
        nint editor = doc.EditorHwnd;
        var (offset, first) = ScintillaInterop.GetScroll(editor);
        int count = Math.Min(ScintillaInterop.GetLineCount(editor) - first, ScintillaInterop.LinesOnScreen(editor) + 1);
        if (doc.MeasuredLines == (first, count)) return;
        doc.MeasuredLines = (first, count);
        for (int i = first; i < first + count; i++)
            doc.MeasuredWidth = Math.Max(doc.MeasuredWidth, ScintillaInterop.MeasureLineWidth(editor, i));
        int lastStart = Math.Max(0, doc.MeasuredWidth - ScintillaInterop.TextViewportWidth(editor));
        if (offset > lastStart) ScintillaInterop.SetHorizontalOffset(editor, lastStart);
    }

    private void SyncDocumentScroll(Document doc)
    {
        if (!_scrollBounds.TryGetValue(doc.Id, out var bounds)) return;
        var horizontal = HorizontalRange(doc);
        bool showHorizontal = horizontal.CanScroll;
        if (doc.HorizontalScroll != showHorizontal)
        {
            doc.HorizontalScroll = showHorizontal;
            if (!_scrollLayoutQueued)
                _scrollLayoutQueued = Win32.PostMessageW(_hwnd, Win32.WM_APP_SCROLL_LAYOUT, 0, 0);
        }

        bool hasVertical = bounds.Vertical.Width > 0 || bounds.Map.Width > 0;
        var vertical = hasVertical ? VerticalRange(doc) : default;
        if (doc.LastVerticalRange != vertical)
        {
            doc.LastVerticalRange = vertical;
            if (bounds.Map.Width > 0 && bounds.Map.Height > 0)
            {
                var mapArea = bounds.Map;
                Win32.InvalidateRect(_hwnd, &mapArea, false);
            }
            if (bounds.Vertical.Width > 0 && bounds.Vertical.Height > 0)
            {
                var vArea = bounds.Vertical;
                Win32.InvalidateRect(_hwnd, &vArea, false);
            }
        }
        if (doc.LastHorizontalRange != horizontal)
        {
            doc.LastHorizontalRange = horizontal;
            var area = bounds.Horizontal;
            if (area.Height > 0) Win32.InvalidateRect(_hwnd, &area, false);
        }
    }

    private void InvalidateScrollbars()
    {
        foreach (var bounds in _scrollBounds.Values)
        {
            var map = bounds.Map;
            var vertical = bounds.Vertical;
            var horizontal = bounds.Horizontal;
            if (map.Width > 0 && map.Height > 0) Win32.InvalidateRect(_hwnd, &map, false);
            if (vertical.Width > 0 && vertical.Height > 0) Win32.InvalidateRect(_hwnd, &vertical, false);
            if (horizontal.Height > 0) Win32.InvalidateRect(_hwnd, &horizontal, false);
        }
    }

    private void InvalidateMinimap(ulong id)
    {
        if (!_scrollBounds.TryGetValue(id, out var bounds)) return;
        if (bounds.Map.Width > 0 && bounds.Map.Height > 0)
        {
            var area = bounds.Map;
            Win32.InvalidateRect(_hwnd, &area, false);
        }
    }

    private void UpdateMinimap(Document doc)
    {
        if (doc.Log is { } log) UpdateLogMinimap(doc.Id, log);
        else InvalidateMinimap(doc.Id);
    }

    private void UpdateLogMinimap(ulong id, LogViewerState log)
    {
        ulong hidden = Math.Min(log.Bookmark.Hidden, log.Bookmark.Size);
        ulong length = log.Bookmark.Size - hidden;
        if (!_settings.Minimap || length == 0 || log.Reading) return;
        const ulong GrowthBucket = 1024 * 1024;
        if ((_mapSamples.TryGetValue(id, out var existing) && existing.Hidden == hidden &&
             (existing.Length == length || (length > GrowthBucket && length > existing.Length && length - existing.Length < GrowthBucket))) ||
            !_mapsLoading.Add(id)) return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            byte[]? samples = null;
            ulong span = 0;
            try
            {
                using var reader = LogReader.Open(log.FilePath, log.EncodingOverride);
                span = Math.Min(length, reader.Size - Math.Min(hidden, reader.Size));
                if (span != 0)
                {
                    samples = new byte[MapSamples];
                    Span<byte> buffer = stackalloc byte[256];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        ulong offset = hidden + SampleOffset(span, i, samples.Length);
                        int read = LogIo.ReadAt(reader.Handle, TextFormat.AlignOffset(offset, reader.Encoding), buffer);
                        int current = 0;
                        int longest = 0;
                        foreach (byte value in buffer[..read])
                        {
                            if (value is 10 or 13) current = 0;
                            else if (value != 0) longest = Math.Max(longest, ++current);
                        }
                        samples[i] = (byte)Math.Min(255, longest);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            DispatchUi(() =>
            {
                _mapsLoading.Remove(id);
                if (!_documents.Exists(item => item.Id == id)) return;
                if (samples is not null && log.Bookmark.Hidden == hidden)
                    _mapSamples[id] = (hidden, span, samples);
                else if (log.Bookmark.Hidden != hidden)
                    UpdateLogMinimap(id, log);
                InvalidateMinimap(id);
            });
        });
    }

    private static ulong SampleOffset(ulong size, int index, int count) =>
        size / (ulong)count * (ulong)index + size % (ulong)count * (ulong)index / (ulong)count;

    private void PaintDocumentScrollbars(nint hdc)
    {
        if (_showHome) return;
        PruneMinimapCaches();

        foreach (var doc in _documents)
        {
            if (!_scrollBounds.TryGetValue(doc.Id, out var bounds)) continue;
            var map = bounds.Map;
            if (map.Width > 0 && map.Height > 0)
            {
                _hits.Add(new HitRect(map, HitKind.ScrollMap, doc.Id));
                if (Win32.RectVisible(hdc, &map)) PaintMinimap(hdc, doc, map);
            }

            var vbar = bounds.Vertical;
            if (vbar.Width > 0 && vbar.Height > 0)
            {
                _hits.Add(new HitRect(vbar, HitKind.VerticalScroll, doc.Id));
                if (Win32.RectVisible(hdc, &vbar)) PaintVerticalScrollbar(hdc, doc, vbar);
            }

            var bar = bounds.Horizontal;
            if (bar.Width > 0 && bar.Height > 0)
            {
                _hits.Add(new HitRect(bar, HitKind.HorizontalScroll, doc.Id));
                if (Win32.RectVisible(hdc, &bar))
                {
                    Win32.FillRect(hdc, &bar, _panelBrush);
                    var (offset, thumbWidth) = HorizontalRange(doc).Thumb(bar.Width, S(18));
                    var thumb = new Win32.RECT
                    {
                        Left = bar.Left + offset,
                        Right = bar.Left + offset + thumbWidth,
                        Top = bar.Top + S(2),
                        Bottom = bar.Bottom - S(2),
                    };
                    uint thumbColor = _hoverKind == HitKind.HorizontalScroll && _hoverTabId == doc.Id
                        ? ThemePalette.AccentHover
                        : ThemePalette.TabActiveBorder;
                    FillRoundRect(hdc, thumb, S(4), thumbColor, thumbColor);
                }
            }
        }
    }

    private void PaintScrollMap(nint hdc) => PaintDocumentScrollbars(hdc);

    private void PaintVerticalScrollbar(nint hdc, Document doc, Win32.RECT area)
    {
        Win32.FillRect(hdc, &area, _panelBrush);
        var range = VerticalRange(doc);
        if (!range.CanScroll) return;

        var (offset, thumbHeight) = range.Thumb(area.Height, S(16));
        var thumb = new Win32.RECT
        {
            Left = area.Left + S(2),
            Right = area.Right - S(2),
            Top = area.Top + offset,
            Bottom = area.Top + offset + thumbHeight,
        };
        bool highlighted = (_hoverKind == HitKind.VerticalScroll && _hoverTabId == doc.Id) ||
                           (_scrollDrag is { Kind: HitKind.VerticalScroll } drag && drag.Id == doc.Id);
        uint color = highlighted ? ThemePalette.AccentHover : ThemePalette.TabActiveBorder;
        FillRoundRect(hdc, thumb, S(4), color, color);
    }

    private void PaintMinimap(nint hdc, Document doc, Win32.RECT area)
    {
        nint bgBrush = Win32.CreateSolidBrush(ThemePalette.MinimapBackground);
        try
        {
            Win32.FillRect(hdc, &area, bgBrush);
        }
        finally
        {
            Win32.DeleteObject(bgBrush);
        }

        if (doc.Log is { } log)
        {
            PaintLogMinimap(hdc, doc, log, area);
            return;
        }

        PaintEditorMinimap(hdc, doc, area);
    }

    private void PaintLogMinimap(nint hdc, Document doc, LogViewerState log, Win32.RECT area)
    {
        var range = VerticalRange(doc);
        if (range.CanScroll)
        {
            var (offset, thumbHeight) = range.Thumb(area.Height, S(12));
            var viewport = new Win32.RECT
            {
                Left = area.Left,
                Right = area.Right,
                Top = area.Top + offset,
                Bottom = area.Top + offset + thumbHeight,
            };
            FillRoundRect(hdc, viewport, S(2), ThemePalette.MinimapViewport, ThemePalette.Border);
        }

        if (_mapSamples.TryGetValue(doc.Id, out var sample) && sample.Hidden == log.Bookmark.Hidden)
        {
            nint brush = Win32.CreateSolidBrush(ThemePalette.LogMinimapInk);
            try
            {
                int pad = S(MinimapPadding);
                int avail = Math.Max(1, area.Height - pad * 2);
                int sampledHeight = (int)(avail * (double)Math.Min(sample.Length, range.Extent) / Math.Max(1, range.Extent));
                int rightLimit = area.Right - pad;
                for (int i = 0; i < sample.Samples.Length; i++)
                {
                    int bytes = sample.Samples[i];
                    if (bytes <= 0) continue;
                    int y = area.Top + pad + i * sampledHeight / sample.Samples.Length;
                    int x = area.Left + pad;
                    var line = new Win32.RECT
                    {
                        Left = x,
                        Right = Math.Min(rightLimit, x + S(1) + bytes / 4),
                        Top = y,
                        Bottom = Math.Min(area.Bottom, y + Math.Max(1, S(1))),
                    };
                    Win32.FillRect(hdc, &line, brush);
                }
            }
            finally
            {
                Win32.DeleteObject(brush);
            }
        }
    }

    private void PaintEditorMinimap(nint hdc, Document doc, Win32.RECT area)
    {
        int pad = S(MinimapPadding);
        int availableHeight = Math.Max(0, area.Height - pad * 2);
        int previewWidth = Math.Max(0, area.Width - pad * 2);
        if (availableHeight <= 0 || previewWidth <= 0) return;

        int lineCount = ScintillaInterop.GetLineCount(doc.EditorHwnd);
        var (rows, previewHeight, navigationHeight) = GetEditorMinimapMetrics(doc, lineCount, availableHeight);
        if (rows == 0) return;

        var range = VerticalRange(doc);
        if (range.CanScroll && navigationHeight > 0)
        {
            double viewportFraction = (double)range.Span / Math.Max(1, range.Extent);
            int minMarker = Math.Min(S(8), navigationHeight);
            int markerHeight = Math.Clamp((int)Math.Round(navigationHeight * viewportFraction), minMarker, navigationHeight);
            double offsetFraction = range.LastStart == 0 ? 0 : Math.Clamp((double)range.Start / range.LastStart, 0, 1);
            int markerTop = area.Top + pad + (int)Math.Round(offsetFraction * (navigationHeight - markerHeight));
            var marker = navigationHeight > previewHeight
                ? new Win32.RECT
                {
                    Left = Math.Max(area.Left, area.Right - Math.Max(2, S(2))),
                    Right = area.Right,
                    Top = markerTop,
                    Bottom = markerTop + markerHeight,
                }
                : new Win32.RECT
                {
                    Left = area.Left,
                    Right = area.Right,
                    Top = markerTop,
                    Bottom = markerTop + markerHeight,
                };
            nint vpBrush = Win32.CreateSolidBrush(ThemePalette.MinimapViewport);
            try
            {
                Win32.FillRect(hdc, &marker, vpBrush);
            }
            finally
            {
                Win32.DeleteObject(vpBrush);
            }
        }

        var cache = EnsureEditorMinimapBitmap(hdc, doc, lineCount, rows);
        if (cache is null || cache.Bitmap == 0) return;

        nint memDc = Win32.CreateCompatibleDC(hdc);
        if (memDc == 0) return;
        nint oldBmp = Win32.SelectObject(memDc, cache.Bitmap);
        try
        {
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1,
            };
            AlphaBlend(hdc, area.Left + pad, area.Top + pad, previewWidth, previewHeight,
                memDc, 0, 0, cache.Width, cache.Height, blend);
        }
        finally
        {
            Win32.SelectObject(memDc, oldBmp);
            Win32.DeleteDC(memDc);
        }
    }

    private (int Rows, int PreviewHeight, int NavigationHeight) GetEditorMinimapMetrics(
        Document doc,
        int lineCount,
        int availableHeight)
    {
        int rowPitch = Math.Max(2, S(MinimapRowPitch));
        int maxByHeight = availableHeight / rowPitch;
        int rows = Math.Min(MapSamples, Math.Min(lineCount, maxByHeight));
        int previewHeight = rows * rowPitch;
        int navigationHeight = previewHeight;
        if (previewHeight < availableHeight && _wrap && lineCount > 0)
        {
            int displayLines = ScintillaInterop.DisplayLineCount(doc.EditorHwnd);
            int visibleLines = ScintillaInterop.LinesOnScreen(doc.EditorHwnd);
            if (displayLines > lineCount && displayLines > visibleLines)
            {
                navigationHeight = Math.Max(previewHeight, availableHeight);
            }
        }
        return (rows, previewHeight, navigationHeight);
    }

    private MinimapCacheEntry? EnsureEditorMinimapBitmap(nint hdc, Document doc, int lineCount, int rows)
    {
        int bmpWidth = MinimapPreviewColumns;
        int bmpHeight = rows * MinimapRowPitch;
        uint ink = ThemePalette.DocumentMinimapInk;

        if (!_minimapPreviews.TryGetValue(doc.Id, out var entry))
        {
            entry = new MinimapCacheEntry();
            _minimapPreviews[doc.Id] = entry;
        }

        if (entry.Bitmap != 0 && entry.Width == bmpWidth && entry.Height == bmpHeight &&
            entry.Revision == doc.Revision && entry.InkColor == ink && entry.Rows == rows)
        {
            return entry;
        }

        if (entry.Bitmap == 0 || entry.Width != bmpWidth || entry.Height != bmpHeight)
        {
            entry.Dispose();
            var bmi = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(Win32.BITMAPINFOHEADER),
                    biWidth = bmpWidth,
                    biHeight = -bmpHeight,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                },
            };
            void* bits = null;
            nint bmp = Win32.CreateDIBSection(hdc, &bmi, 0, &bits, 0, 0);
            if (bmp == 0 || bits is null) return null;
            entry.Bitmap = bmp;
            entry.Bits = (nint)bits;
            entry.Width = bmpWidth;
            entry.Height = bmpHeight;
        }

        entry.Revision = doc.Revision;
        entry.InkColor = ink;
        entry.Rows = rows;

        uint* pixels = (uint*)entry.Bits;
        new Span<uint>(pixels, bmpWidth * bmpHeight).Clear();

        uint letterPixel = PremultipliedBgra(ink, 65);
        uint punctPixel = PremultipliedBgra(ink, 40);
        const int TabWidth = 4;
        int lastLineIndex = Math.Max(0, lineCount - 1);

        for (int row = 0; row < rows; row++)
        {
            int lineIndex = rows == 1 ? 0 : (int)((long)row * lastLineIndex / (rows - 1));
            var lineBytes = ScintillaInterop.LineBytes(doc.EditorHwnd, lineIndex);
            if (lineBytes.IsEmpty) continue;

            string lineText = Encoding.UTF8.GetString(lineBytes[..Math.Min(lineBytes.Length, MinimapPreviewColumns * 4)]);
            uint* scanline = pixels + row * MinimapRowPitch * bmpWidth;
            int col = 0;
            foreach (char ch in lineText)
            {
                if (col >= MinimapPreviewColumns) break;
                if (ch == '\t')
                {
                    col += TabWidth - (col % TabWidth);
                }
                else
                {
                    if (!char.IsWhiteSpace(ch))
                    {
                        scanline[col] = char.IsLetterOrDigit(ch) ? letterPixel : punctPixel;
                    }
                    col++;
                }
            }
        }

        return entry;

        static uint PremultipliedBgra(uint bgr, uint opacityPercent)
        {
            uint r = bgr & 0xFF;
            uint g = (bgr >> 8) & 0xFF;
            uint b = (bgr >> 16) & 0xFF;
            uint a = 255 * opacityPercent / 100;
            return (a << 24) | ((r * a / 255) << 16) | ((g * a / 255) << 8) | (b * a / 255);
        }
    }

    private void PruneMinimapCaches()
    {
        if (_minimapPreviews.Count == 0) return;
        List<ulong>? stale = null;
        foreach (ulong id in _minimapPreviews.Keys)
        {
            if (!_documents.Exists(doc => doc.Id == id))
            {
                stale ??= [];
                stale.Add(id);
            }
        }
        if (stale is null) return;
        foreach (ulong id in stale)
        {
            if (_minimapPreviews.Remove(id, out var entry)) entry.Dispose();
        }
    }

    private void BeginScroll(HitRect hit, int x, int y)
    {
        bool vertical = hit.Kind is HitKind.ScrollMap or HitKind.VerticalScroll;
        var doc = _documents.Find(item => item.Id == hit.TabId);
        if (doc is null) return;
        if (_activeId != hit.TabId) SelectTab(hit.TabId);

        var range = vertical ? VerticalRange(doc) : HorizontalRange(doc);
        if (!range.CanScroll) return;

        var area = hit.Rect;
        if (hit.Kind == HitKind.ScrollMap && doc.Log is null)
        {
            int pad = S(MinimapPadding);
            int avail = Math.Max(0, area.Height - pad * 2);
            if (avail <= 0) return;
            int lineCount = ScintillaInterop.GetLineCount(doc.EditorHwnd);
            var (_, _, navHeight) = GetEditorMinimapMetrics(doc, lineCount, avail);
            if (navHeight <= 0 || y < area.Top + pad || y > area.Top + pad + navHeight) return;

            int origin = area.Top + pad;
            int pointer = y - origin;
            ulong target = (ulong)Math.Clamp(
                Math.Round((double)pointer / navHeight * range.LastStart),
                0,
                range.LastStart);
            MoveDocumentScroll(doc, vertical: true, range, target);
            _scrollDrag = new ScrollDrag(doc.Id, HitKind.ScrollMap, y, y, 0, navHeight, range, origin, navHeight);
            Win32.SetCapture(_hwnd);
            return;
        }

        int originPx = vertical ? area.Top : area.Left;
        int length = vertical ? area.Height : area.Width;
        int ptr = (vertical ? y : x) - originPx;
        var (offset, size) = range.Thumb(length, S(vertical ? 16 : 18));
        if (ptr >= offset && ptr < offset + size)
        {
            int grab = vertical ? y : x;
            _scrollDrag = new ScrollDrag(doc.Id, hit.Kind, grab, grab, offset, length - size, range, originPx, length);
            Win32.SetCapture(_hwnd);
        }
        else
        {
            MoveDocumentScroll(doc, vertical, range, range.Seek(ptr, length));
        }
    }

    private void DragDocumentScroll(int x, int y)
    {
        if (_scrollDrag is not { } drag) return;
        var doc = _documents.Find(item => item.Id == drag.Id);
        if (doc is null) return;
        bool vertical = drag.Kind is HitKind.ScrollMap or HitKind.VerticalScroll;
        int pointer = vertical ? y : x;
        if (pointer == drag.LastPointer) return;
        _scrollDrag = drag with { LastPointer = pointer };

        if (drag.Kind == HitKind.ScrollMap && doc.Log is null)
        {
            int rel = Math.Clamp(pointer - drag.Origin, 0, Math.Max(1, drag.Length));
            ulong target = (ulong)Math.Clamp(
                Math.Round((double)rel / Math.Max(1, drag.Length) * drag.Range.LastStart),
                0,
                drag.Range.LastStart);
            MoveDocumentScroll(doc, vertical: true, drag.Range, target);
            return;
        }

        int delta = pointer - drag.Pointer;
        ulong position = drag.Range.Drag(delta, drag.Offset, drag.Travel);
        MoveDocumentScroll(doc, vertical, drag.Range, position);
    }

    private bool TryStartMapDrag(HitRect hit, int x, int y)
    {
        if (hit.Kind is not (HitKind.ScrollMap or HitKind.VerticalScroll or HitKind.HorizontalScroll)) return false;
        BeginScroll(hit, x, y);
        return true;
    }

    private bool HandleMapMove(int x, int y)
    {
        if (_scrollDrag is null) return false;
        DragDocumentScroll(x, y);
        return true;
    }

    private bool EndMapDrag(int x, int y)
    {
        if (_scrollDrag is null) return false;
        DragDocumentScroll(x, y);
        _scrollDrag = null;
        Win32.ReleaseCapture();
        return true;
    }

    private void MoveDocumentScroll(Document doc, bool vertical, ScrollRange range, ulong position)
    {
        if (!vertical)
        {
            if (doc.Viewport is { } viewport) viewport.SetHorizontalOffset((int)position);
            else ScintillaInterop.SetHorizontalOffset(doc.EditorHwnd, (int)position);
        }
        else if (doc.Log is { } log)
        {
            if (doc.ScrollDestination is null && position == VerticalRange(doc).Start) return;
            var destination = position == 0 ? LogDestination.Start : position >= range.LastStart
                ? LogDestination.End : LogDestination.AtOffset(log.Bookmark.Hidden + position);
            if (log.Reading)
            {
                doc.ScrollDestination = destination;
                return;
            }
            doc.ScrollDestination = null;
            if (log.IsEnabled) log.Navigate(destination);
        }
        else
        {
            ScintillaInterop.SetFirstVisibleLine(doc.EditorHwnd, (int)position);
        }
        SyncDocumentScroll(doc);
    }

    private void FlushLogScroll(Document doc)
    {
        if (doc.ScrollDestination is not { } destination || doc.Log is not { IsEnabled: true } log) return;
        doc.ScrollDestination = null;
        log.Navigate(destination);
    }
}
