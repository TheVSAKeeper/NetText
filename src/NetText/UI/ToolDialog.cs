using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe class ToolDialog
{
    private const string ClassName = "NetTextToolDialog";
    private const uint BS_AUTOCHECKBOX = 0x0003;
    private const uint CBS_DROPDOWNLIST = 0x0003;
    private const uint BM_GETCHECK = 0x00F0;
    private const uint BM_SETCHECK = 0x00F1;
    private const uint CB_ADDSTRING = 0x0143;
    private const uint CB_GETCURSEL = 0x0147;
    private const uint CB_RESETCONTENT = 0x014B;
    private const uint CB_SETCURSEL = 0x014E;
    private const uint LB_SETCURSEL = 0x0186;
    private const uint ES_MULTILINE = 0x0004;
    private const uint ES_AUTOVSCROLL = 0x0040;
    private const uint ES_READONLY = 0x0800;

    private static readonly Dictionary<nint, ToolDialog> Windows = [];
    private static nint _panelBrush = Win32.CreateSolidBrush(ThemePalette.Panel);
    private static nint _fieldBrush = Win32.CreateSolidBrush(ThemePalette.Field);
    private static nint _borderBrush = Win32.CreateSolidBrush(ThemePalette.Border);
    private static nint _hoverBrush = Win32.CreateSolidBrush(ThemePalette.Hover);
    private static nint _accentBrush = Win32.CreateSolidBrush(ThemePalette.Accent);
    private static nint _chromeBrush = Win32.CreateSolidBrush(ThemePalette.Chrome);
    private static ushort _class;

    private readonly nint _instance;
    private readonly nint _font;
    private readonly uint _dpi;
    private readonly Dictionary<int, Action> _commands = [];
    private int _id;
    private nint _firstControl;
    private nint _window;
    private Action? _accept;
    private Action? _cancel;

    public bool RestoreOwnerFocus { get; set; } = true;

    private ToolDialog(nint owner, string title, int width, int height)
    {
        _instance = Win32.GetModuleHandleW(null);
        _dpi = Math.Max(96, Win32.GetDpiForWindow(owner));
        if (_class == 0)
        {
            fixed (char* name = ClassName)
            {
                var cls = new Win32.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                    hInstance = _instance,
                    lpfnWndProc = &WindowProc,
                    lpszClassName = name,
                    hCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW),
                    hbrBackground = _panelBrush,
                };
                _class = Win32.RegisterClassExW(&cls);
            }
        }

        Win32.RECT bounds;
        Win32.GetWindowRect(owner, &bounds);
        _window = Win32.CreateWindowExW(Win32.WS_EX_DLGMODALFRAME, ClassName, title,
            Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_SYSMENU,
            bounds.Left + (bounds.Width - Scale(width)) / 2, bounds.Top + (bounds.Height - Scale(height)) / 2,
            Scale(width), Scale(height), owner, 0, _instance, null);
        if (_window == 0) throw new InvalidOperationException("Не удалось создать окно инструмента.");
        Windows.Add(_window, this);
        Win32.EnableDarkTitleBar(_window);
        Win32.SetWindowBorderColor(_window, ThemePalette.Chrome);
        Win32.SetWindowTheme(_window, "DarkMode_Explorer", null);
        _font = Win32.CreateFontW(-Scale(14), 0, 0, 0, Win32.FW_NORMAL, 0, 0, 0,
            Win32.DEFAULT_CHARSET, 0, 0, Win32.CLEARTYPE_QUALITY, Win32.DEFAULT_PITCH, "Segoe UI");
    }

    public static void RefreshTheme()
    {
        ReplaceBrush(ref _panelBrush, ThemePalette.Panel);
        ReplaceBrush(ref _fieldBrush, ThemePalette.Field);
        ReplaceBrush(ref _borderBrush, ThemePalette.Border);
        ReplaceBrush(ref _hoverBrush, ThemePalette.Hover);
        ReplaceBrush(ref _accentBrush, ThemePalette.Accent);
        ReplaceBrush(ref _chromeBrush, ThemePalette.Chrome);

        foreach (var hwnd in Windows.Keys)
        {
            Win32.SetWindowBorderColor(hwnd, ThemePalette.Chrome);
            Win32.InvalidateRect(hwnd, null, true);
        }

        static void ReplaceBrush(ref nint brush, uint color)
        {
            if (brush != 0) Win32.DeleteObject(brush);
            brush = Win32.CreateSolidBrush(color);
        }
    }

    public static void Show(nint owner, string title, int width, int height, Action<ToolDialog> build)
    {
        var dialog = new ToolDialog(owner, title, width, height);
        nint previousFocus = Win32.GetFocus();
        try
        {
            build(dialog);
            Win32.EnableWindow(owner, false);
            Win32.ShowWindow(dialog._window, Win32.SW_SHOW);
            Win32.SetFocus(dialog._firstControl);
            while (dialog._window != 0)
            {
                Win32.MSG message;
                int result = Win32.GetMessageW(&message, 0, 0, 0);
                if (result <= 0)
                {
                    if (result == 0) Win32.PostQuitMessage((int)message.wParam);
                    break;
                }
                if (message.message == Win32.WM_KEYDOWN)
                {
                    if (message.wParam == Win32.VK_ESCAPE)
                    {
                        dialog.Cancel();
                        continue;
                    }
                    if (message.wParam == Win32.VK_RETURN && dialog._accept is { } accept &&
                        !Win32.IsKeyDown(Win32.VK_CONTROL) &&
                        (Win32.GetWindowLongPtrW(message.hwnd, -16) & 4) == 0)
                    {
                        dialog.Run(accept);
                        continue;
                    }
                }
                if (dialog._window != 0 && Win32.IsDialogMessageW(dialog._window, &message)) continue;
                Win32.TranslateMessage(&message);
                Win32.DispatchMessageW(&message);
            }
        }
        finally
        {
            Win32.EnableWindow(owner, true);
            dialog.Close();
            if (dialog.RestoreOwnerFocus) Win32.SetFocus(previousFocus == 0 ? owner : previousFocus);
            Win32.DeleteObject(dialog._font);
        }
    }

    public nint Handle => _window;

    public void Close()
    {
        if (_window != 0) Win32.DestroyWindow(_window);
    }

    public void OnCancel(Action cancel) => _cancel = cancel;

    public void SetTitle(string title)
    {
        if (_window != 0) Win32.SetWindowTextW(_window, title);
    }

    public void Resize(int width, int height)
    {
        if (_window == 0) return;
        Win32.RECT bounds;
        Win32.GetWindowRect(_window, &bounds);
        Win32.MoveWindow(_window, bounds.Left, bounds.Top, Scale(width), Scale(height), true);
    }

    private void Cancel()
    {
        if (_cancel is { } cancel) cancel();
        else Close();
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            if (_window != 0) Win32.MessageBoxW(_window, error.Message, "NetText", Win32.MB_ICONWARNING);
        }
    }

    public nint Label(string text, int x, int y, int w, int h = 22) =>
        Control("STATIC", text, 0, x, y, w, h);

    public nint Edit(string text, int x, int y, int w, int h = 26, bool multiline = false, bool readOnly = false) =>
        Control("EDIT", text, Win32.WS_TABSTOP | Win32.WS_BORDER |
            (multiline ? ES_MULTILINE | ES_AUTOVSCROLL | Win32.WS_VSCROLL : Win32.ES_AUTOHSCROLL) |
            (readOnly ? ES_READONLY : 0), x, y, w, h);

    public nint List(int x, int y, int w, int h, Action? select = null) =>
        Control("LISTBOX", "", Win32.WS_TABSTOP | Win32.WS_BORDER | Win32.WS_VSCROLL | Win32.LBS_NOTIFY | Win32.LBS_NOINTEGRALHEIGHT,
            x, y, w, h, select, Win32.LBN_DBLCLK);

    public nint ListBox(int x, int y, int w, int h, Action? onSelect = null, Action? onDoubleClick = null)
    {
        nint control = Control("LISTBOX", "", Win32.WS_TABSTOP | Win32.WS_BORDER | Win32.WS_VSCROLL | Win32.LBS_NOTIFY | Win32.LBS_NOINTEGRALHEIGHT,
            x, y, w, h, onSelect, Win32.LBN_SELCHANGE);
        if (onDoubleClick is not null) _commands[_id | (Win32.LBN_DBLCLK << 16)] = onDoubleClick;
        return control;
    }

    public nint ComboBox(int x, int y, int w, int h = 220, Action? onChange = null) =>
        Control("COMBOBOX", "", Win32.WS_TABSTOP | Win32.WS_VSCROLL | CBS_DROPDOWNLIST,
            x, y, w, h, onChange, 1);

    public nint CheckBox(string text, bool checkedState, int x, int y, int w, int h = 24, Action<bool>? onToggle = null)
    {
        nint handle = 0;
        handle = Control("BUTTON", text, Win32.WS_TABSTOP | BS_AUTOCHECKBOX, x, y, w, h,
            onToggle is null ? null : () => onToggle(IsChecked(handle)));
        SetChecked(handle, checkedState);
        return handle;
    }

    public nint Button(string text, int x, int y, int w, Action action, bool primary = false)
    {
        if (primary) _accept = action;
        return Control("BUTTON", text, Win32.WS_TABSTOP | (primary ? Win32.BS_DEFPUSHBUTTON : 0), x, y, w, 29, action);
    }

    public nint Button(string text, int x, int y, int w, int h, Action action, bool primary = false)
    {
        if (primary) _accept = action;
        return Control("BUTTON", text, Win32.WS_TABSTOP | (primary ? Win32.BS_DEFPUSHBUTTON : 0), x, y, w, h, action);
    }

    public void SetBounds(nint control, int x, int y, int w, int h)
    {
        if (control != 0) Win32.MoveWindow(control, Scale(x), Scale(y), Scale(w), Scale(h), true);
    }

    private nint Control(string cls, string text, uint style, int x, int y, int w, int h, Action? action = null, int code = 0)
    {
        int id = ++_id;
        var control = Win32.CreateWindowExW(0, cls, text, Win32.WS_CHILD | Win32.WS_VISIBLE | style,
            Scale(x), Scale(y), Scale(w), Scale(h), _window, id, _instance, null);
        Win32.SendMessageW(control, Win32.WM_SETFONT, (nuint)_font, 1);
        Win32.SetWindowTheme(control, "DarkMode_Explorer", null);
        if (_firstControl == 0 && (style & Win32.WS_TABSTOP) != 0) _firstControl = control;
        if (action is not null) _commands.Add(id | (code << 16), action);
        return control;
    }

    public static int Selection(nint control) => (int)Win32.SendMessageW(control, Win32.LB_GETCURSEL, 0, 0);

    public static void SetSelection(nint control, int index) =>
        Win32.SendMessageW(control, LB_SETCURSEL, (nuint)Math.Max(-1, index), 0);

    public static void SetItems(nint control, IEnumerable<string> items)
    {
        Win32.SendMessageW(control, Win32.LB_RESETCONTENT, 0, 0);
        foreach (string item in items)
        {
            fixed (char* value = item) Win32.SendMessageW(control, Win32.LB_ADDSTRING, 0, (nint)value);
        }
        Win32.SendMessageW(control, LB_SETCURSEL, 0, 0);
    }

    public static void SetComboItems(nint control, IEnumerable<string> items, int selectedIndex = 0)
    {
        Win32.SendMessageW(control, CB_RESETCONTENT, 0, 0);
        int count = 0;
        foreach (string item in items)
        {
            fixed (char* value = item) Win32.SendMessageW(control, CB_ADDSTRING, 0, (nint)value);
            count++;
        }
        if (count > 0)
        {
            Win32.SendMessageW(control, CB_SETCURSEL, (nuint)Math.Clamp(selectedIndex, 0, count - 1), 0);
        }
    }

    public static int ComboSelection(nint control) => (int)Win32.SendMessageW(control, CB_GETCURSEL, 0, 0);

    public static void SetComboSelection(nint control, int index) =>
        Win32.SendMessageW(control, CB_SETCURSEL, (nuint)Math.Max(-1, index), 0);

    public static bool IsChecked(nint control) => Win32.SendMessageW(control, BM_GETCHECK, 0, 0) == 1;

    public static void SetChecked(nint control, bool value) =>
        Win32.SendMessageW(control, BM_SETCHECK, value ? 1u : 0u, 0);

    public static void SetVisible(nint control, bool visible)
    {
        if (control != 0) Win32.ShowWindow(control, visible ? Win32.SW_SHOW : Win32.SW_HIDE);
    }

    public static void ShowGroup(bool visible, params nint[] controls)
    {
        foreach (nint control in controls) SetVisible(control, visible);
    }

    public static void SetEnabled(nint control, bool enabled)
    {
        if (control != 0) Win32.EnableWindow(control, enabled);
    }

    public static void SetText(nint control, string text)
    {
        if (control != 0) Win32.SetWindowTextW(control, text);
    }

    public static string Text(nint control)
    {
        int length = Win32.GetWindowTextLengthW(control);
        var buffer = new char[length + 1];
        fixed (char* pointer = buffer)
            return new string(pointer, 0, Win32.GetWindowTextW(control, pointer, buffer.Length));
    }

    private int Scale(int value) => (int)Math.Round(value * (_dpi / 96.0));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (Windows.TryGetValue(hwnd, out var dialog))
        {
            switch (message)
            {
                case Win32.WM_ERASEBKGND:
                {
                    Win32.RECT rc;
                    Win32.GetClientRect(hwnd, &rc);
                    Win32.FillRect((nint)wParam, &rc, _panelBrush);
                    return 1;
                }
                case Win32.WM_CLOSE:
                    dialog.Cancel();
                    return 0;
                case Win32.WM_NCDESTROY:
                    dialog._window = 0;
                    Windows.Remove(hwnd);
                    break;
                case Win32.WM_COMMAND:
                    if (dialog._commands.TryGetValue((int)((ulong)wParam & 0xFFFFFFFF), out var action))
                    {
                        dialog.Run(action);
                    }
                    return 0;
                case Win32.WM_CTLCOLORSTATIC:
                case Win32.WM_CTLCOLORBTN:
                    Win32.SetTextColor((nint)wParam, ThemePalette.Text);
                    Win32.SetBkColor((nint)wParam, ThemePalette.Panel);
                    return _panelBrush;
                case Win32.WM_CTLCOLOREDIT:
                case Win32.WM_CTLCOLORLISTBOX:
                    Win32.SetTextColor((nint)wParam, ThemePalette.Text);
                    Win32.SetBkColor((nint)wParam, ThemePalette.Field);
                    return _fieldBrush;
            }
        }
        return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
