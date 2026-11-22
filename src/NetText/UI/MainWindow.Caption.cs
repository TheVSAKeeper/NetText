using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private int _captionHover;
    private int _captionPressed;
    private const int CaptionButtonWidth = 44;
    private const int ApplicationIconResource = 32512;
    private const int SmallIconLogicalSize = 16;
    private nint _smallWindowIcon;
    private nint _largeWindowIcon;

    private void ApplyActiveTheme(string themeName)
    {
        ThemePalette.Apply(themeName);

        ReplaceBrush(ref _bgBrush, ThemePalette.Bg);
        ReplaceBrush(ref _panelBrush, ThemePalette.Panel);
        ReplaceBrush(ref _tabBarBrush, ThemePalette.TabBar);

        if (_hwnd != 0)
        {
            Win32.SetWindowBorderColor(_hwnd, ThemePalette.Chrome);
        }

        ToolDialog.RefreshTheme();
        ClearEditorIndicators();

        int pt = (int)MathF.Round(_fontSize);
        foreach (var doc in _documents)
        {
            if (doc.Log is null)
            {
                if (doc.EditorHwnd != 0)
                {
                    ScintillaInterop.ApplyLanguage(doc.EditorHwnd, doc.File?.Path, pt);
                }
                if (doc.FilterPreviewHwnd != 0)
                {
                    ScintillaInterop.ApplyFontAndTheme(doc.FilterPreviewHwnd, pt);
                }
                RefreshJsonIndicator(doc);
                UpdateEditorIndicators(doc);
                UpdateEditorLinksAndLineNumbers(doc);
                InvalidateMinimap(doc.Id);
            }
            else
            {
                if (doc.Viewport is { } viewport)
                {
                    Win32.InvalidateRect(viewport.Hwnd, null, false);
                }
                else if (doc.EditorHwnd != 0)
                {
                    ScintillaInterop.ApplyFontAndTheme(doc.EditorHwnd, pt);
                }
                InvalidateMinimap(doc.Id);
            }
        }

        if (_hwnd != 0)
        {
            Win32.InvalidateRect(_hwnd, null, true);
            InvalidateChrome();
            InvalidateScrollbars();
        }

        static void ReplaceBrush(ref nint brush, uint color)
        {
            if (brush != 0) Win32.DeleteObject(brush);
            brush = Win32.CreateSolidBrush(color);
        }
    }

    private void PaintTitleBarBackground(nint hdc, Win32.RECT rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        uint chrome = ThemePalette.Chrome;
        uint glow = ThemePalette.TitleGlow;
        int x42 = rect.Left + (int)Math.Round(rect.Width * 0.42);
        int x80 = rect.Left + (int)Math.Round(rect.Width * 0.80);

        if (chrome == glow)
        {
            nint solid = Win32.CreateSolidBrush(chrome);
            Win32.FillRect(hdc, &rect, solid);
            Win32.DeleteObject(solid);
        }
        else
        {
            Win32.TRIVERTEX* vertices = stackalloc Win32.TRIVERTEX[6];
            vertices[0] = MakeVertex(rect.Left, rect.Top, chrome);
            vertices[1] = MakeVertex(x42, rect.Bottom, chrome);
            vertices[2] = MakeVertex(x42, rect.Top, chrome);
            vertices[3] = MakeVertex(x80, rect.Bottom, glow);
            vertices[4] = MakeVertex(x80, rect.Top, glow);
            vertices[5] = MakeVertex(rect.Right, rect.Bottom, chrome);

            Win32.GRADIENT_RECT* mesh = stackalloc Win32.GRADIENT_RECT[3];
            mesh[0] = new Win32.GRADIENT_RECT { UpperLeft = 0, LowerRight = 1 };
            mesh[1] = new Win32.GRADIENT_RECT { UpperLeft = 2, LowerRight = 3 };
            mesh[2] = new Win32.GRADIENT_RECT { UpperLeft = 4, LowerRight = 5 };
            Win32.GradientFill(hdc, vertices, 6, mesh, 3, Win32.GRADIENT_FILL_RECT_H);
        }

        if (ThemePalette.HasTitleMotif)
        {
            PaintViceCityPalms(hdc, rect);
        }

        static Win32.TRIVERTEX MakeVertex(int x, int y, uint bgr) => new()
        {
            x = x,
            y = y,
            Red = (ushort)((bgr & 0xFF) << 8),
            Green = (ushort)(((bgr >> 8) & 0xFF) << 8),
            Blue = (ushort)(((bgr >> 16) & 0xFF) << 8),
            Alpha = 0,
        };
    }

    private void PaintViceCityPalms(nint hdc, Win32.RECT rect)
    {
        int zoneWidth = S(58);
        int rightEdge = Math.Min(rect.Right, CaptionMenuRight(rect.Width) - S(6));
        int ox = rightEdge - zoneWidth;
        int oy = rect.Top;
        if (ox < rect.Left + S(12)) return;

        nint brush = Win32.CreateSolidBrush(ThemePalette.TitleMotif);
        nint pen = Win32.CreatePen(Win32.PS_SOLID, 1, ThemePalette.TitleMotif);
        nint oldBrush = Win32.SelectObject(hdc, brush);
        nint oldPen = Win32.SelectObject(hdc, pen);
        try
        {
            // Left taller palm silhouette
            DrawPoly(hdc, ox, oy, [
                (14, 36), (17, 25), (19, 15), (16, 13), (9, 16), (6, 19), (10, 13), (17, 10),
                (11, 7), (7, 8), (13, 5), (20, 8), (22, 3), (24, 9), (30, 5), (33, 7),
                (26, 10), (34, 12), (36, 16), (29, 13), (23, 12), (26, 17), (27, 21), (22, 16),
                (20, 26), (19, 36),
            ]);

            // Right smaller palm silhouette
            DrawPoly(hdc, ox, oy, [
                (38, 36), (40, 28), (41, 20), (38, 19), (33, 22), (31, 24), (34, 18), (40, 16),
                (35, 13), (32, 14), (37, 11), (42, 14), (44, 10), (46, 15), (51, 12), (54, 13),
                (47, 16), (54, 18), (56, 22), (50, 19), (45, 18), (47, 23), (48, 26), (44, 21),
                (43, 29), (42, 36),
            ]);
        }
        finally
        {
            Win32.SelectObject(hdc, oldBrush);
            Win32.SelectObject(hdc, oldPen);
            Win32.DeleteObject(brush);
            Win32.DeleteObject(pen);
        }

        void DrawPoly(nint dc, int originX, int originY, ReadOnlySpan<(int X, int Y)> points)
        {
            Win32.POINT* apt = stackalloc Win32.POINT[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                apt[i] = new Win32.POINT { X = originX + S(points[i].X), Y = originY + S(points[i].Y) };
            }
            Win32.Polygon(dc, apt, points.Length);
        }
    }

    private void UpdateWindowIcons()
    {
        SetIcon(ref _smallWindowIcon, Win32.ICON_SMALL, S(SmallIconLogicalSize));
        SetIcon(ref _largeWindowIcon, Win32.ICON_BIG, S(32));

        void SetIcon(ref nint current, uint kind, int size)
        {
            nint icon = Win32.LoadImageW(_hInstance, ApplicationIconResource, Win32.IMAGE_ICON, size, size, 0);
            if (icon == 0) return;
            Win32.SendMessageW(_hwnd, Win32.WM_SETICON, kind, icon);
            if (current != 0) Win32.DestroyIcon(current);
            current = icon;
        }
    }

    private void ReleaseWindowIcons()
    {
        if (_smallWindowIcon != 0) Win32.DestroyIcon(_smallWindowIcon);
        if (_largeWindowIcon != 0) Win32.DestroyIcon(_largeWindowIcon);
    }

    private static nint CalculateCaptionFrame(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var client = (Win32.RECT*)lParam;
        int top = client->Top;
        int left = client->Left;
        Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        client->Top = Win32.IsZoomed(hwnd) ? top + Math.Max(0, client->Left - left) : top;
        return 0;
    }

    private nint CaptionHitTest(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        nint native = Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        if (native >= Win32.HT_LEFT && native <= Win32.HT_BOTTOMRIGHT) return native;

        var point = new Win32.POINT { X = Win32.GetXParam(lParam), Y = Win32.GetYParam(lParam) };
        Win32.ScreenToClient(hwnd, &point);
        if (!Win32.IsZoomed(hwnd) && point.Y >= 0 && point.Y < S(5)) return Win32.HT_TOP;
        int button = CaptionButtonAt(point.X, point.Y);
        if (button != 0) return button;
        if (point.Y >= 0 && point.Y < S(ToolbarLogicalHeight) && HitTest(point.X, point.Y) is null)
            return Win32.HT_CAPTION;
        return Win32.HT_CLIENT;
    }

    private int CaptionMenuRight(int width) => Math.Max(0, width - 3 * S(CaptionButtonWidth));

    private int CaptionButtonAt(int x, int y)
    {
        if (y < 0 || y >= S(ToolbarLogicalHeight) || _hwnd == 0) return 0;
        Win32.RECT bounds;
        Win32.GetClientRect(_hwnd, &bounds);
        if (x < CaptionMenuRight(bounds.Width) || x >= bounds.Right) return 0;
        int slot = (bounds.Right - 1 - x) / S(CaptionButtonWidth);
        return slot switch { 0 => Win32.HT_CLOSE, 1 => Win32.HT_MAXBUTTON, _ => Win32.HT_MINBUTTON };
    }

    private void TrackCaptionButton(int hit, bool trackLeave = true)
    {
        int button = IsCaptionButton(hit) ? hit : 0;
        if (button == _captionHover) return;
        _captionHover = button;
        InvalidateChrome();
        if (button == 0 || !trackLeave) return;
        var track = new Win32.TRACKMOUSEEVENT
        {
            cbSize = (uint)sizeof(Win32.TRACKMOUSEEVENT),
            dwFlags = Win32.TME_LEAVE | Win32.TME_NONCLIENT,
            hwndTrack = _hwnd,
        };
        Win32.TrackMouseEvent(&track);
    }

    private static bool IsCaptionButton(int hit) => hit is Win32.HT_MINBUTTON or Win32.HT_MAXBUTTON or Win32.HT_CLOSE;

    private bool BeginCaptionButton(int hit)
    {
        if (!IsCaptionButton(hit)) return false;
        _captionPressed = hit;
        TrackCaptionButton(hit, trackLeave: false);
        Win32.SetCapture(_hwnd);
        return true;
    }

    private bool ReleaseCaptionButton(int x, int y)
    {
        if (_captionPressed == 0) return false;
        int button = _captionPressed;
        _captionPressed = 0;
        Win32.ReleaseCapture();
        int hit = CaptionButtonAt(x, y);
        TrackCaptionButton(hit);
        if (hit == button)
        {
            uint command = button switch
            {
                Win32.HT_MINBUTTON => Win32.SC_MINIMIZE,
                Win32.HT_MAXBUTTON => Win32.IsZoomed(_hwnd) ? Win32.SC_RESTORE : Win32.SC_MAXIMIZE,
                _ => Win32.SC_CLOSE,
            };
            Win32.PostMessageW(_hwnd, Win32.WM_SYSCOMMAND, command, 0);
        }
        return true;
    }

    private void PaintCaptionButtons(nint hdc, int width, int height)
    {
        int buttonWidth = S(CaptionButtonWidth);
        int x = CaptionMenuRight(width);
        Paint(Win32.HT_MINBUTTON);
        Paint(Win32.HT_MAXBUTTON);
        Paint(Win32.HT_CLOSE);

        void Paint(int hit)
        {
            bool hovered = _captionHover == hit;
            if (hovered)
            {
                var button = new Win32.RECT { Left = x + S(2), Right = x + buttonWidth - S(2), Top = S(4), Bottom = height - S(4) };
                uint color = _captionPressed == hit ? ThemePalette.SelectedBg : ThemePalette.HoverBg;
                FillRoundRect(hdc, button, S(5), color, color);
            }
            int cx = x + buttonWidth / 2;
            int cy = height / 2;
            nint pen = Win32.CreatePen(Win32.PS_SOLID, S(1), hovered ? ThemePalette.Text : ThemePalette.Muted);
            nint previousPen = Win32.SelectObject(hdc, pen);
            switch (hit)
            {
                case Win32.HT_MINBUTTON:
                    Stroke(-5, 2, 6, 2);
                    break;
                case Win32.HT_MAXBUTTON when Win32.IsZoomed(_hwnd):
                    Stroke(-2, -5, 5, -5);
                    Stroke(5, -5, 5, 3);
                    Box(-5, -2, 2, 5);
                    break;
                case Win32.HT_MAXBUTTON:
                    Box(-5, -5, 5, 5);
                    break;
                case Win32.HT_CLOSE:
                    Stroke(-5, -5, 6, 6);
                    Stroke(-5, 5, 6, -6);
                    break;
            }
            Win32.SelectObject(hdc, previousPen);
            Win32.DeleteObject(pen);
            x += buttonWidth;

            void Stroke(int x1, int y1, int x2, int y2)
            {
                Win32.MoveToEx(hdc, cx + S(x1), cy + S(y1), null);
                Win32.LineTo(hdc, cx + S(x2), cy + S(y2));
            }

            void Box(int left, int top, int right, int bottom)
            {
                Stroke(left, top, right, top);
                Stroke(right, top, right, bottom);
                Stroke(right, bottom, left, bottom);
                Stroke(left, bottom, left, top);
            }
        }
    }
}
