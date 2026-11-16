using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetText.Native;

namespace NetText.UI;

public static unsafe class GoToLineDialog
{
    private const int IdEdit = 101;
    private const int IdHint = 102;
    private const int IdCancel = 2;
    private const int IdConfirm = 1;

    private sealed class DialogState
    {
        public int LineCount;
        public nint Hwnd;
        public nint EditHwnd;
        public nint HintHwnd;
        public nint UiFont;
        public nint SmallFont;
        public nint BgBrush;
        public nint EditBgBrush;
        public bool Invalid;
        public bool Done;
        public int? ResultLine;
    }

    private static ushort _classAtom;
    private static readonly nint BgBrushStatic = Win32.CreateSolidBrush(ThemePalette.Panel);

    public static int? Show(nint ownerHwnd, int lineCount)
    {
        var hInstance = Win32.GetModuleHandleW(null);
        EnsureClassRegistered(hInstance);

        uint dpi = Win32.GetDpiForWindow(ownerHwnd);
        if (dpi == 0) dpi = 96;
        float scale = dpi / 96.0f;
        int S(int px) => (int)MathF.Round(px * scale);

        int width = S(360);
        int height = S(162);

        Win32.RECT ownerRect;
        Win32.GetWindowRect(ownerHwnd, &ownerRect);
        int x = ownerRect.Left + (ownerRect.Width - width) / 2;
        int y = ownerRect.Top + (ownerRect.Height - height) / 2;

        var state = new DialogState
        {
            LineCount = Math.Max(1, lineCount),
            BgBrush = BgBrushStatic,
            EditBgBrush = Win32.CreateSolidBrush(ThemePalette.Bg),
            UiFont = Win32.CreateFontW(-S(14), 0, 0, 0, Win32.FW_NORMAL, 0, 0, 0, Win32.DEFAULT_CHARSET, 0, 0, Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH, "Segoe UI"),
            SmallFont = Win32.CreateFontW(-S(12), 0, 0, 0, Win32.FW_NORMAL, 0, 0, 0, Win32.DEFAULT_CHARSET, 0, 0, Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH, "Segoe UI"),
        };

        var handle = GCHandle.Alloc(state);
        try
        {
            Win32.EnableWindow(ownerHwnd, false);
            var hwnd = Win32.CreateWindowExW(
                Win32.WS_EX_DLGMODALFRAME,
                "NetTextGoToLine",
                "Перейти к строке",
                Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_SYSMENU | Win32.WS_VISIBLE,
                x, y, width, height,
                ownerHwnd,
                0,
                hInstance,
                (void*)GCHandle.ToIntPtr(handle));

            if (hwnd == 0)
            {
                return null;
            }

            Win32.EnableDarkTitleBar(hwnd);

            Win32.RECT client;
            Win32.GetClientRect(hwnd, &client);
            int pad = S(16);
            int innerW = client.Width - pad * 2;

            state.EditHwnd = Win32.CreateWindowExW(
                0,
                "EDIT",
                "",
                Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_BORDER | Win32.WS_TABSTOP | Win32.ES_AUTOHSCROLL,
                pad, pad, innerW, S(28),
                hwnd,
                IdEdit,
                hInstance,
                null);
            Win32.SendMessageW(state.EditHwnd, Win32.WM_SETFONT, (nuint)state.UiFont, 1);

            state.HintHwnd = Win32.CreateWindowExW(
                0,
                "STATIC",
                $"Строки с 1 по {state.LineCount}",
                Win32.WS_CHILD | Win32.WS_VISIBLE,
                pad, pad + S(36), innerW, S(20),
                hwnd,
                IdHint,
                hInstance,
                null);
            Win32.SendMessageW(state.HintHwnd, Win32.WM_SETFONT, (nuint)state.SmallFont, 1);

            int btnW = S(92);
            int btnH = S(30);
            int btnY = client.Height - pad - btnH;

            var cancelBtn = Win32.CreateWindowExW(
                0,
                "BUTTON",
                "Отмена",
                Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_TABSTOP | Win32.BS_PUSHBUTTON,
                client.Width - pad - btnW * 2 - S(8), btnY, btnW, btnH,
                hwnd,
                IdCancel,
                hInstance,
                null);
            Win32.SendMessageW(cancelBtn, Win32.WM_SETFONT, (nuint)state.UiFont, 1);

            var okBtn = Win32.CreateWindowExW(
                0,
                "BUTTON",
                "Перейти",
                Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_TABSTOP | Win32.BS_DEFPUSHBUTTON,
                client.Width - pad - btnW, btnY, btnW, btnH,
                hwnd,
                IdConfirm,
                hInstance,
                null);
            Win32.SendMessageW(okBtn, Win32.WM_SETFONT, (nuint)state.UiFont, 1);

            Win32.SetFocus(state.EditHwnd);

            Win32.MSG msg;
            while (!state.Done && Win32.GetMessageW(&msg, 0, 0, 0) > 0)
            {
                if (msg.message == Win32.WM_KEYDOWN)
                {
                    if ((int)msg.wParam == Win32.VK_RETURN)
                    {
                        TryConfirm(state);
                        continue;
                    }
                    if ((int)msg.wParam == Win32.VK_ESCAPE)
                    {
                        state.Done = true;
                        Win32.DestroyWindow(hwnd);
                        continue;
                    }
                }

                Win32.TranslateMessage(&msg);
                Win32.DispatchMessageW(&msg);
            }

            return state.ResultLine;
        }
        finally
        {
            Win32.EnableWindow(ownerHwnd, true);
            Win32.SetFocus(ownerHwnd);
            Win32.DeleteObject(state.EditBgBrush);
            Win32.DeleteObject(state.UiFont);
            Win32.DeleteObject(state.SmallFont);
            handle.Free();
        }
    }

    public static int? LineIndex(string? value, int lineCount)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!uint.TryParse(value.Trim(), out uint number) || number == 0 || number > (uint)lineCount)
        {
            return null;
        }

        return (int)(number - 1);
    }

    private static void TryConfirm(DialogState state)
    {
        int len = Win32.GetWindowTextLengthW(state.EditHwnd);
        var buf = new char[len + 1];
        string text;
        fixed (char* ptr = buf)
        {
            int actual = Win32.GetWindowTextW(state.EditHwnd, ptr, buf.Length);
            text = new string(ptr, 0, actual);
        }

        var line = LineIndex(text, state.LineCount);
        if (line is null)
        {
            state.Invalid = true;
            Win32.SetWindowTextW(state.HintHwnd, $"Введите целое число от 1 до {state.LineCount}.");
            Win32.InvalidateRect(state.HintHwnd, null, true);
            Win32.SetFocus(state.EditHwnd);
            return;
        }

        state.ResultLine = line;
        state.Done = true;
        Win32.DestroyWindow(state.Hwnd);
    }

    private static void EnsureClassRegistered(nint hInstance)
    {
        if (_classAtom != 0)
        {
            return;
        }

        fixed (char* cls = "NetTextGoToLine")
        {
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                style = Win32.CS_HREDRAW | Win32.CS_VREDRAW,
                lpfnWndProc = &DlgWndProc,
                hInstance = hInstance,
                hCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW),
                hbrBackground = BgBrushStatic,
                lpszClassName = cls,
            };
            _classAtom = Win32.RegisterClassExW(&wc);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint DlgWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == Win32.WM_CREATE)
        {
            nint createParam = *(nint*)lParam;
            Win32.SetWindowLongPtrW(hwnd, Win32.GWL_USERDATA, createParam);
            if (createParam != 0)
            {
                var st = (DialogState)GCHandle.FromIntPtr(createParam).Target!;
                st.Hwnd = hwnd;
            }
            return 0;
        }

        nint userData = Win32.GetWindowLongPtrW(hwnd, Win32.GWL_USERDATA);
        var state = userData != 0 ? GCHandle.FromIntPtr(userData).Target as DialogState : null;

        switch (msg)
        {
            case Win32.WM_COMMAND:
                int id = (int)((ulong)wParam & 0xFFFF);
                if (state is not null)
                {
                    if (id == IdConfirm)
                    {
                        TryConfirm(state);
                        return 0;
                    }
                    if (id == IdCancel)
                    {
                        state.Done = true;
                        Win32.DestroyWindow(hwnd);
                        return 0;
                    }
                }
                break;

            case Win32.WM_CTLCOLOREDIT:
                if (state is not null)
                {
                    Win32.SetTextColor((nint)wParam, ThemePalette.Text);
                    Win32.SetBkColor((nint)wParam, ThemePalette.Bg);
                    return state.EditBgBrush;
                }
                break;

            case Win32.WM_CTLCOLORSTATIC:
                if (state is not null)
                {
                    Win32.SetTextColor((nint)wParam, state.Invalid ? ThemePalette.Error : ThemePalette.Muted);
                    Win32.SetBkMode((nint)wParam, Win32.TRANSPARENT);
                    return state.BgBrush;
                }
                break;

            case Win32.WM_CLOSE:
                if (state is not null)
                {
                    state.Done = true;
                }
                Win32.DestroyWindow(hwnd);
                return 0;
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
