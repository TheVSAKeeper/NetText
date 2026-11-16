using System.Runtime.InteropServices;

namespace NetText.Native;

internal static unsafe partial class Win32
{
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_TABSTOP = 0x00010000;
    public const uint WS_BORDER = 0x00800000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_VSCROLL = 0x00200000;

    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_DLGMODALFRAME = 0x00000001;

    public const uint ES_AUTOHSCROLL = 0x0080;
    public const uint BS_DEFPUSHBUTTON = 0x0001;
    public const uint BS_PUSHBUTTON = 0x0000;
    public const uint LBS_NOTIFY = 0x0001;
    public const uint LBS_NOINTEGRALHEIGHT = 0x0100;

    public const int EN_CHANGE = 0x0300;
    public const int LBN_SELCHANGE = 1;
    public const int LBN_DBLCLK = 2;
    public const uint LB_ADDSTRING = 0x0180;
    public const uint LB_RESETCONTENT = 0x0184;
    public const uint LB_GETCURSEL = 0x0188;
    public const uint WM_TIMER = 0x0113;

    public const uint CS_HREDRAW = 0x0002;
    public const uint CS_VREDRAW = 0x0001;
    public const uint CS_DBLCLKS = 0x0008;

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    public const uint WM_CREATE = 0x0001;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_SETFOCUS = 0x0007;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_ACTIVATEAPP = 0x001C;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_SETFONT = 0x0030;
    public const uint WM_NOTIFY = 0x004E;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_CTLCOLOREDIT = 0x0133;
    public const uint WM_CTLCOLORLISTBOX = 0x0134;
    public const uint WM_CTLCOLORBTN = 0x0135;
    public const uint WM_CTLCOLORSTATIC = 0x0138;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_CAPTURECHANGED = 0x0215;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_DROPFILES = 0x0233;
    public const uint WM_APP = 0x8000;
    public const uint WM_APP_DISPATCH = WM_APP + 1;
    public const uint WM_APP_IPC = WM_APP + 2;

    public const int VK_RETURN = 0x0D;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_PRIOR = 0x21;
    public const int VK_NEXT = 0x22;
    public const int VK_END = 0x23;
    public const int VK_HOME = 0x24;
    public const int VK_CONTROL = 0x11;
    public const int VK_SHIFT = 0x10;
    public const int VK_F3 = 0x72;
    public const int VK_OEM_PLUS = 0xBB;
    public const int VK_OEM_MINUS = 0xBD;
    public const int VK_ADD = 0x6B;
    public const int VK_SUBTRACT = 0x6D;

    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_TOPALIGN = 0x0000;
    public const uint TPM_RETURNCMD = 0x0100;

    public const uint MF_STRING = 0x00000000;
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_DISABLED = 0x00000002;
    public const uint MF_POPUP = 0x00000010;
    public const uint MF_SEPARATOR = 0x00000800;

    public const uint MB_YESNO = 0x00000004;
    public const uint MB_ICONWARNING = 0x00000030;
    public const int IDYES = 6;

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public const int TRANSPARENT = 1;
    public const int PS_SOLID = 0;

    public const uint DT_LEFT = 0x00000000;
    public const uint DT_CENTER = 0x00000001;
    public const uint DT_RIGHT = 0x00000002;
    public const uint DT_VCENTER = 0x00000004;
    public const uint DT_SINGLELINE = 0x00000020;
    public const uint DT_END_ELLIPSIS = 0x00008000;
    public const uint DT_NOPREFIX = 0x00000800;

    public const int FW_NORMAL = 400;
    public const int FW_SEMIBOLD = 600;
    public const int DEFAULT_CHARSET = 1;
    public const int CLEARTYPE_QUALITY = 5;
    public const int DEFAULT_PITCH = 0;

    public const uint OFN_ALLOWMULTISELECT = 0x00000200;
    public const uint OFN_EXPLORER = 0x00080000;
    public const uint OFN_FILEMUSTEXIST = 0x00001000;
    public const uint OFN_PATHMUSTEXIST = 0x00000800;
    public const uint OFN_OVERWRITEPROMPT = 0x00000002;
    public const uint OFN_NOCHANGEDIR = 0x00000008;

    public const uint TME_LEAVE = 0x00000002;

    public const int IDC_ARROW = 32512;
    public const int IDC_HAND = 32649;

    public const int GWL_STYLE = -16;
    public const int GWL_USERDATA = -21;

    public const uint TTS_ALWAYSTIP = 0x01;
    public const uint TTS_NOPREFIX = 0x02;
    public const uint TTF_SUBCLASS = 0x0010;
    public const uint TTF_IDISHWND = 0x0001;
    public const uint TTM_ADDTOOLW = 0x0432;
    public const uint TTM_UPDATETIPTEXTW = 0x0439;
    public const uint TTM_SETMAXTIPWIDTH = 0x0418;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint EM_SETSEL = 0x00B1;
    public const uint EM_SETCUEBANNER = 0x1501;
    public const uint SW_MAXIMIZE = 3;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NMHDR
    {
        public nint hwndFrom;
        public nuint idFrom;
        public uint code;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCNotification
    {
        public NMHDR nmhdr;
        public nint position;
        public int ch;
        public int modifiers;
        public int modificationType;
        public nint text;
        public nint length;
        public nint linesAdded;
        public int message;
        public nuint wParam;
        public nint lParam;
        public nint line;
        public int foldLevelNow;
        public int foldLevelPrev;
        public int margin;
        public int listType;
        public int x;
        public int y;
        public int token;
        public nint annotationLinesAdded;
        public int updated;
        public int listCompletionMethod;
        public int characterSource;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OPENFILENAMEW
    {
        public uint lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public char* lpstrFilter;
        public char* lpstrCustomFilter;
        public uint nMaxCustFilter;
        public uint nFilterIndex;
        public char* lpstrFile;
        public uint nMaxFile;
        public char* lpstrFileTitle;
        public uint nMaxFileTitle;
        public char* lpstrInitialDir;
        public char* lpstrTitle;
        public uint Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        public char* lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public char* lpTemplateName;
        public void* pvReserved;
        public uint dwReserved;
        public uint FlagsEx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOOLINFOW
    {
        public uint cbSize;
        public uint uFlags;
        public nint hwnd;
        public nuint uId;
        public RECT rect;
        public nint hinst;
        public char* lpszText;
        public nint lParam;
        public void* lpReserved;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint LoadLibraryW(string lpLibFileName);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int X,
        int Y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        void* lpParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableWindow(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

    [LibraryImport("user32.dll")]
    public static partial nint SetFocus(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsChild(nint hWndParent, nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hWnd, RECT* lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveWindow(nint hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hWnd, RECT* lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, RECT* lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hWnd, POINT* lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hWnd, POINT* lpPoint);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint hWnd, uint Msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(MSG* lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(MSG* lpMsg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(MSG* lpMsg);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hWnd, uint Msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint hWnd, nuint uIDEvent);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendMessageW(nint hWnd, uint Msg, nuint wParam, string lParam);

    [LibraryImport("user32.dll")]
    public static partial nint SendMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int nVirtKey);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(nint value);

    [LibraryImport("user32.dll")]
    public static partial nint LoadCursorW(nint hInstance, nint lpCursorName);

    [LibraryImport("user32.dll")]
    public static partial nint SetCursor(nint hCursor);

    [LibraryImport("user32.dll")]
    public static partial nint SetCapture(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackMouseEvent(TRACKMOUSEEVENT* lpEventTrack);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint hMenu);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, void* lptpm);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowTextW(nint hWnd, string lpString);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowTextW(nint hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowTextLengthW(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint BeginPaint(nint hWnd, PAINTSTRUCT* lpPaint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hWnd, PAINTSTRUCT* lpPaint);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(nint hDC, RECT* lprc, nint hbr);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawTextW(nint hdc, string lpchText, int cchText, RECT* lprc, uint format);

    [LibraryImport("user32.dll")]
    public static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreatePen(int iStyle, int cWidth, uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RoundRect(nint hdc, int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveToEx(nint hdc, int x, int y, POINT* lppt);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LineTo(nint hdc, int x, int y);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    [LibraryImport("gdi32.dll")]
    public static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetBkColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTextExtentPoint32W(nint hdc, string lpString, int c, SIZE* psizl);

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TEXTMETRICW
    {
        public int tmHeight;
        public int tmAscent;
        public int tmDescent;
        public int tmInternalLeading;
        public int tmExternalLeading;
        public int tmAveCharWidth;
        public int tmMaxCharWidth;
        public int tmWeight;
        public int tmOverhang;
        public int tmDigitizedAspectX;
        public int tmDigitizedAspectY;
        public char tmFirstChar;
        public char tmLastChar;
        public char tmDefaultChar;
        public char tmBreakChar;
        public byte tmItalic;
        public byte tmUnderlined;
        public byte tmStruckOut;
        public byte tmPitchAndFamily;
        public byte tmCharSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCROLLINFO
    {
        public uint cbSize;
        public uint fMask;
        public int nMin;
        public int nMax;
        public uint nPage;
        public int nPos;
        public int nTrackPos;
    }

    public const uint WS_HSCROLL = 0x00100000;
    public const int SB_HORZ = 0;
    public const int SB_VERT = 1;
    public const uint SIF_RANGE = 0x0001;
    public const uint SIF_PAGE = 0x0002;
    public const uint SIF_POS = 0x0004;
    public const uint SIF_TRACKPOS = 0x0010;
    public const uint SIF_ALL = SIF_RANGE | SIF_PAGE | SIF_POS | SIF_TRACKPOS;
    public const int SB_LINEUP = 0;
    public const int SB_LINEDOWN = 1;
    public const int SB_PAGEUP = 2;
    public const int SB_PAGEDOWN = 3;
    public const int SB_THUMBPOSITION = 4;
    public const int SB_THUMBTRACK = 5;
    public const int SB_TOP = 6;
    public const int SB_BOTTOM = 7;

    public const uint ETO_CLIPPED = 0x0004;
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint WM_VSCROLL = 0x0115;
    public const uint WM_HSCROLL = 0x0114;

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint hdc, BITMAPINFO* pbmi, uint usage, void** ppvBits, nint hSection, uint offset);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ExtTextOutW(nint hdc, int x, int y, uint options, RECT* lprect, char* lpString, uint c, int* lpDx);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTextExtentPoint32W(nint hdc, char* lpString, int c, SIZE* psizl);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTextMetricsW(nint hdc, TEXTMETRICW* lptm);

    public const uint WM_NCCREATE = 0x0081;
    public const uint WM_NCDESTROY = 0x0082;
    public const int GWLP_USERDATA = -21;
    public const int IDC_IBEAM = 32513;
    public const uint SRCCOPY = 0x00CC0020;
    public const int VK_INSERT = 0x2D;

    public static int MulDiv(int number, int numerator, int denominator) =>
        (int)((long)number * numerator / Math.Max(1, denominator));

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("user32.dll")]
    public static partial int SetScrollInfo(nint hwnd, int nBar, SCROLLINFO* lpsi, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetScrollInfo(nint hwnd, int nBar, SCROLLINFO* lpsi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    public static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalFree(nint hMem);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int dwAttribute, int* pvAttribute, int cbAttribute);

    [LibraryImport("shell32.dll")]
    public static partial void DragAcceptFiles(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAccept);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint DragQueryFileW(nint hDrop, uint iFile, char* lpszFile, uint cch);

    [LibraryImport("shell32.dll")]
    public static partial void DragFinish(nint hDrop);

    [LibraryImport("comdlg32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetOpenFileNameW(OPENFILENAMEW* lpofn);

    [LibraryImport("comdlg32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSaveFileNameW(OPENFILENAMEW* lpofn);

    public static bool IsKeyDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    public static void EnableDarkTitleBar(nint hwnd)
    {
        int enabled = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &enabled, sizeof(int));
    }

    public static int GetXParam(nint lParam) => unchecked((short)((long)lParam & 0xFFFF));
    public static int GetYParam(nint lParam) => unchecked((short)(((long)lParam >> 16) & 0xFFFF));
    public static short GetWheelDelta(nuint wParam) => unchecked((short)(((ulong)wParam >> 16) & 0xFFFF));
}
