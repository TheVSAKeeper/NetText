using System.Runtime.InteropServices;
using System.Text;
using NetText.UI;

namespace NetText.Native;

internal static unsafe partial class ScintillaInterop
{
    public const uint SCI_ADDTEXT = 2001;
    public const uint SCI_CLEARALL = 2004;
    public const uint SCI_GETLENGTH = 2006;
    public const uint SCI_GETCURRENTPOS = 2008;
    public const uint SCI_GETANCHOR = 2009;
    public const uint SCI_SETUNDOCOLLECTION = 2012;
    public const uint SCI_SELECTALL = 2013;
    public const uint SCI_SETSAVEPOINT = 2014;
    public const uint SCI_GOTOLINE = 2024;
    public const uint SCI_SETANCHOR = 2026;
    public const uint SCI_SETEOLMODE = 2031;
    public const uint SCI_SETBUFFEREDDRAW = 2035;
    public const uint SCI_SETCODEPAGE = 2037;
    public const uint SCI_STYLECLEARALL = 2050;
    public const uint SCI_STYLESETFORE = 2051;
    public const uint SCI_STYLESETBACK = 2052;
    public const uint SCI_STYLESETBOLD = 2053;
    public const uint SCI_STYLESETSIZE = 2055;
    public const uint SCI_STYLESETFONT = 2056;
    public const uint SCI_SETSELBACK = 2068;
    public const uint SCI_SETCARETFORE = 2069;
    public const uint SCI_BEGINUNDOACTION = 2078;
    public const uint SCI_ENDUNDOACTION = 2079;
    public const uint SCI_SETCARETLINEVISIBLE = 2096;
    public const uint SCI_SETCARETLINEBACK = 2098;
    public const uint SCI_GETCOLUMN = 2129;
    public const uint SCI_SETCURRENTPOS = 2141;
    public const uint SCI_GETSELECTIONSTART = 2143;
    public const uint SCI_GETSELECTIONEND = 2145;
    public const uint SCI_GETFIRSTVISIBLELINE = 2152;
    public const uint SCI_GETLINECOUNT = 2154;
    public const uint SCI_SETMARGINLEFT = 2155;
    public const uint SCI_GETMODIFY = 2159;
    public const uint SCI_SETSEL = 2160;
    public const uint SCI_LINEFROMPOSITION = 2166;
    public const uint SCI_SCROLLCARET = 2169;
    public const uint SCI_REPLACESEL = 2170;
    public const uint SCI_SETREADONLY = 2171;
    public const uint SCI_EMPTYUNDOBUFFER = 2175;
    public const uint SCI_SETTEXT = 2181;
    public const uint SCI_GETTEXT = 2182;
    public const uint SCI_SETCARETWIDTH = 2188;
    public const uint SCI_SETTARGETSTART = 2190;
    public const uint SCI_SETTARGETEND = 2192;
    public const uint SCI_REPLACETARGET = 2194;
    public const uint SCI_SETMARGINTYPEN = 2240;
    public const uint SCI_SETMARGINWIDTHN = 2242;
    public const uint SCI_SETMARGINCURSORN = 2248;
    public const uint SCI_SETMARGINBACKN = 2250;
    public const uint SCI_SETWRAPMODE = 2268;
    public const uint SCI_SETSCROLLWIDTH = 2274;
    public const uint SCI_TEXTWIDTH = 2276;
    public const uint SCI_SETXOFFSET = 2397;
    public const uint SCI_GETXOFFSET = 2398;
    public const uint SCI_ALLOCATE = 2446;
    public const uint SCI_SETSCROLLWIDTHTRACKING = 2516;
    public const uint SCI_GETCHARACTERPOINTER = 2520;
    public const uint SCI_SETEXTRAASCENT = 2525;
    public const uint SCI_SETEXTRADESCENT = 2527;
    public const uint SCI_SETMULTIPLESELECTION = 2563;
    public const uint SCI_SETADDITIONALSELECTIONTYPING = 2565;
    public const uint SCI_SETADDITIONALCARETSBLINK = 2567;
    public const uint SCI_SETFIRSTVISIBLELINE = 2613;
    public const uint SCI_SETTECHNOLOGY = 2630;
    public const uint SCI_SETCARETLINEVISIBLEALWAYS = 2655;
    public const uint SCI_COLOURISE = 4003;
    public const uint SCI_SETKEYWORDS = 4005;
    public const uint SCI_SETILEXER = 4033;

    public const int SC_CP_UTF8 = 65001;
    public const int SC_EOL_LF = 2;
    public const int SC_MARGIN_NUMBER = 1;
    public const int SC_CURSORARROW = 2;
    public const int STYLE_DEFAULT = 32;
    public const int STYLE_LINENUMBER = 33;
    public const int SC_WRAP_NONE = 0;
    public const int SC_WRAP_WORD = 1;
    public const int SC_TECHNOLOGY_DIRECTWRITE = 1;

    public const uint SCN_SAVEPOINTREACHED = 2002;
    public const uint SCN_SAVEPOINTLEFT = 2003;
    public const uint SCN_UPDATEUI = 2007;
    public const uint SCN_PAINTED = 2013;

    [LibraryImport("Lexilla.dll", EntryPoint = "CreateLexer")]
    private static partial nint CreateLexerNative(byte* name);

    private static bool _scintillaLoaded;
    private static bool _lexillaLoaded;

    public static void EnsureLoaded()
    {
        if (!_scintillaLoaded)
        {
            var baseDir = AppContext.BaseDirectory;
            if (Win32.LoadLibraryW(Path.Combine(baseDir, "Scintilla.dll")) == 0)
            {
                Win32.LoadLibraryW("Scintilla.dll");
            }
            _scintillaLoaded = true;
        }
    }

    private static void EnsureLexillaLoaded()
    {
        if (!_lexillaLoaded)
        {
            var baseDir = AppContext.BaseDirectory;
            if (Win32.LoadLibraryW(Path.Combine(baseDir, "Lexilla.dll")) == 0)
            {
                Win32.LoadLibraryW("Lexilla.dll");
            }
            _lexillaLoaded = true;
        }
    }

    public static nint CreateEditorControl(nint parent, nint hInstance, int fontSizePt, bool wrap)
    {
        EnsureLoaded();
        var hwnd = Win32.CreateWindowExW(
            0,
            "Scintilla",
            "",
            Win32.WS_CHILD | Win32.WS_CLIPCHILDREN | Win32.WS_TABSTOP,
            0, 0, 100, 100,
            parent,
            0,
            hInstance,
            null);

        Send(hwnd, SCI_SETCODEPAGE, SC_CP_UTF8, 0);
        Send(hwnd, SCI_SETTECHNOLOGY, SC_TECHNOLOGY_DIRECTWRITE, 0);
        Send(hwnd, SCI_SETBUFFEREDDRAW, 0, 0);
        Send(hwnd, SCI_SETEOLMODE, SC_EOL_LF, 0);
        Send(hwnd, SCI_SETMULTIPLESELECTION, 1, 0);
        Send(hwnd, SCI_SETADDITIONALSELECTIONTYPING, 1, 0);
        Send(hwnd, SCI_SETADDITIONALCARETSBLINK, 1, 0);
        Send(hwnd, SCI_SETSCROLLWIDTH, 1, 0);
        Send(hwnd, SCI_SETSCROLLWIDTHTRACKING, 1, 0);
        Send(hwnd, SCI_SETMARGINLEFT, 0, 8);
        Send(hwnd, SCI_SETEXTRAASCENT, 2, 0);
        Send(hwnd, SCI_SETEXTRADESCENT, 2, 0);

        Send(hwnd, SCI_SETMARGINTYPEN, 0, SC_MARGIN_NUMBER);
        Send(hwnd, SCI_SETMARGINCURSORN, 0, SC_CURSORARROW);
        Send(hwnd, SCI_SETMARGINBACKN, 0, (nint)ThemePalette.Bg);

        Send(hwnd, SCI_SETCARETFORE, ThemePalette.Text, 0);
        Send(hwnd, SCI_SETCARETWIDTH, 2, 0);
        Send(hwnd, SCI_SETCARETLINEVISIBLE, 1, 0);
        Send(hwnd, SCI_SETCARETLINEVISIBLEALWAYS, 1, 0);
        Send(hwnd, SCI_SETCARETLINEBACK, ThemePalette.CaretLineBg, 0);
        Send(hwnd, SCI_SETSELBACK, 1, (nint)ThemePalette.SelectionBg);
        Send(hwnd, SCI_SETWRAPMODE, (nuint)(wrap ? SC_WRAP_WORD : SC_WRAP_NONE), 0);

        ApplyFontAndTheme(hwnd, fontSizePt);
        return hwnd;
    }

    public static void ApplyFontAndTheme(nint hwnd, int fontSizePt)
    {
        SetStyleFont(hwnd, STYLE_DEFAULT, "Consolas");
        Send(hwnd, SCI_STYLESETSIZE, STYLE_DEFAULT, fontSizePt);
        Send(hwnd, SCI_STYLESETFORE, STYLE_DEFAULT, (nint)ThemePalette.Text);
        Send(hwnd, SCI_STYLESETBACK, STYLE_DEFAULT, (nint)ThemePalette.Bg);
        Send(hwnd, SCI_STYLECLEARALL, 0, 0);

        Send(hwnd, SCI_STYLESETFORE, STYLE_LINENUMBER, (nint)ThemePalette.Dim);
        Send(hwnd, SCI_STYLESETBACK, STYLE_LINENUMBER, (nint)ThemePalette.Bg);
        UpdateLineNumberMarginWidth(hwnd);
    }

    public static void UpdateLineNumberMarginWidth(nint hwnd)
    {
        int lines = Math.Max(1, (int)Send(hwnd, SCI_GETLINECOUNT, 0, 0));
        int digits = Math.Max(3, (int)Math.Floor(Math.Log10(lines)) + 1);
        var sample = new string('9', digits) + " ";
        var bytes = Encoding.UTF8.GetBytes(sample + "\0");
        fixed (byte* ptr = bytes)
        {
            int width = (int)Send(hwnd, SCI_TEXTWIDTH, STYLE_LINENUMBER, (nint)ptr);
            Send(hwnd, SCI_SETMARGINWIDTHN, 0, width + 12);
        }
    }

    public static void SetTextUtf8(nint hwnd, ReadOnlySpan<byte> utf8Bytes)
    {
        Send(hwnd, SCI_SETUNDOCOLLECTION, 0, 0);
        Send(hwnd, SCI_CLEARALL, 0, 0);
        if (!utf8Bytes.IsEmpty)
        {
            Send(hwnd, SCI_ALLOCATE, (nuint)utf8Bytes.Length, 0);
            fixed (byte* ptr = utf8Bytes)
            {
                Send(hwnd, SCI_ADDTEXT, (nuint)utf8Bytes.Length, (nint)ptr);
            }
        }
        Send(hwnd, SCI_SETUNDOCOLLECTION, 1, 0);
        Send(hwnd, SCI_EMPTYUNDOBUFFER, 0, 0);
        Send(hwnd, SCI_SETSAVEPOINT, 0, 0);
        Send(hwnd, SCI_GOTOLINE, 0, 0);
        UpdateLineNumberMarginWidth(hwnd);
    }

    public static byte[] CopyUtf8Bytes(nint hwnd)
    {
        int length = (int)Send(hwnd, SCI_GETLENGTH, 0, 0);
        if (length <= 0)
        {
            return [];
        }

        byte* ptr = (byte*)Send(hwnd, SCI_GETCHARACTERPOINTER, 0, 0);
        if (ptr is not null)
        {
            return new ReadOnlySpan<byte>(ptr, length).ToArray();
        }

        var buffer = new byte[length + 1];
        fixed (byte* dst = buffer)
        {
            Send(hwnd, SCI_GETTEXT, (nuint)(length + 1), (nint)dst);
        }
        return buffer[..length];
    }

    public static (byte[] Buffer, int Length) CopyUtf8BytesPooled(nint hwnd)
    {
        int length = (int)Send(hwnd, SCI_GETLENGTH, 0, 0);
        if (length <= 0)
        {
            return (Array.Empty<byte>(), 0);
        }

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(length + 1);
        byte* ptr = (byte*)Send(hwnd, SCI_GETCHARACTERPOINTER, 0, 0);
        if (ptr is not null)
        {
            new ReadOnlySpan<byte>(ptr, length).CopyTo(buffer);
            return (buffer, length);
        }

        fixed (byte* dst = buffer)
        {
            Send(hwnd, SCI_GETTEXT, (nuint)(length + 1), (nint)dst);
        }
        return (buffer, length);
    }

    public static (int Line, int Column) GetCursorPosition(nint hwnd)
    {
        nint pos = Send(hwnd, SCI_GETCURRENTPOS, 0, 0);
        int line = (int)Send(hwnd, SCI_LINEFROMPOSITION, (nuint)pos, 0);
        int col = (int)Send(hwnd, SCI_GETCOLUMN, (nuint)pos, 0);
        return (line, col);
    }

    public static int GetLineCount(nint hwnd) => Math.Max(1, (int)Send(hwnd, SCI_GETLINECOUNT, 0, 0));

    public static void GoToLine(nint hwnd, int zeroBasedLine)
    {
        Send(hwnd, SCI_GOTOLINE, (nuint)zeroBasedLine, 0);
    }

    public static void SetWrap(nint hwnd, bool wrap)
    {
        Send(hwnd, SCI_SETWRAPMODE, (nuint)(wrap ? SC_WRAP_WORD : SC_WRAP_NONE), 0);
    }

    public static void SetSavePoint(nint hwnd)
    {
        Send(hwnd, SCI_SETSAVEPOINT, 0, 0);
    }

    public static nint CreateLogViewerControl(nint parent, nint hInstance, int fontSizePt, bool wrap)
    {
        var hwnd = CreateEditorControl(parent, hInstance, fontSizePt, wrap);
        Send(hwnd, SCI_SETMARGINWIDTHN, 0, 0);
        Send(hwnd, SCI_SETILEXER, 0, 0);
        Send(hwnd, SCI_SETREADONLY, 1, 0);
        return hwnd;
    }

    public static void SetLogPageText(nint hwnd, ReadOnlySpan<byte> utf8Bytes)
    {
        Send(hwnd, SCI_SETREADONLY, 0, 0);
        SetTextUtf8(hwnd, utf8Bytes);
        Send(hwnd, SCI_SETMARGINWIDTHN, 0, 0);
        Send(hwnd, SCI_SETREADONLY, 1, 0);
    }

    public static int GetByteLength(nint hwnd) => (int)Send(hwnd, SCI_GETLENGTH, 0, 0);

    public static string GetTextString(nint hwnd)
    {
        var bytes = CopyUtf8Bytes(hwnd);
        return bytes.Length == 0 ? "" : Encoding.UTF8.GetString(bytes);
    }

    public static (int Start, int End, bool Reversed) GetSelection(nint hwnd)
    {
        int anchor = (int)Send(hwnd, SCI_GETANCHOR, 0, 0);
        int caret = (int)Send(hwnd, SCI_GETCURRENTPOS, 0, 0);
        return anchor <= caret
            ? (anchor, caret, false)
            : (caret, anchor, true);
    }

    public static void SetSelection(nint hwnd, int start, int end, bool reversed)
    {
        int anchor = reversed ? end : start;
        int caret = reversed ? start : end;
        Send(hwnd, SCI_SETSEL, (nuint)anchor, caret);
        Send(hwnd, SCI_SCROLLCARET, 0, 0);
    }

    public static string GetSelectedText(nint hwnd)
    {
        var (start, end, _) = GetSelection(hwnd);
        if (end <= start)
        {
            return "";
        }
        var bytes = CopyUtf8Bytes(hwnd);
        int safeStart = Math.Clamp(start, 0, bytes.Length);
        int safeEnd = Math.Clamp(end, safeStart, bytes.Length);
        return Encoding.UTF8.GetString(bytes.AsSpan(safeStart, safeEnd - safeStart));
    }

    public static bool TryFormatDocumentJsonUtf8(nint hwnd, int start, int end, ReadOnlySpan<byte> newline, out bool changed, out string error)
    {
        int len = (int)Send(hwnd, SCI_GETLENGTH, 0, 0);
        int safeStart = Math.Clamp(start, 0, len);
        int safeEnd = Math.Clamp(end, safeStart, len);
        int sliceLen = safeEnd - safeStart;

        byte* docPtr = sliceLen > 0 ? (byte*)Send(hwnd, SCI_GETCHARACTERPOINTER, 0, 0) : null;
        byte[]? fallback = null;
        ReadOnlySpan<byte> source;
        if (docPtr is not null || sliceLen == 0)
        {
            source = sliceLen == 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(docPtr + safeStart, sliceLen);
        }
        else
        {
            fallback = CopyUtf8Bytes(hwnd);
            source = fallback.AsSpan(safeStart, sliceLen);
        }

        if (!NetText.Core.JsonFormat.TryFormatUtf8Native(source, newline, out byte* formattedPtr, out int formattedLen, out error))
        {
            changed = false;
            return false;
        }

        try
        {
            var formattedSpan = new ReadOnlySpan<byte>(formattedPtr, formattedLen);
            if (formattedSpan.SequenceEqual(source))
            {
                changed = false;
                return true;
            }

            Send(hwnd, SCI_BEGINUNDOACTION, 0, 0);
            try
            {
                Send(hwnd, SCI_SETTARGETSTART, (nuint)safeStart, 0);
                Send(hwnd, SCI_SETTARGETEND, (nuint)safeEnd, 0);
                Send(hwnd, SCI_REPLACETARGET, (nuint)formattedLen, formattedLen == 0 ? 0 : (nint)formattedPtr);
                if (safeEnd > safeStart || safeStart > 0)
                {
                    SetSelection(hwnd, safeStart, safeStart + formattedLen, false);
                }
            }
            finally
            {
                Send(hwnd, SCI_ENDUNDOACTION, 0, 0);
            }

            changed = true;
            return true;
        }
        finally
        {
            NativeMemory.Free(formattedPtr);
        }
    }

    public static void ReplaceByteRange(nint hwnd, int start, int end, string replacement)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(replacement.Length);
        byte[]? rented = maxBytes > 512 ? System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes) : null;
        Span<byte> scratch = rented is not null ? rented.AsSpan(0, maxBytes) : stackalloc byte[512];
        try
        {
            int len = Encoding.UTF8.GetBytes(replacement, scratch);
            ReplaceByteRangeUtf8(hwnd, start, end, scratch[..len]);
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static void ReplaceByteRangeUtf8(nint hwnd, int start, int end, ReadOnlySpan<byte> utf8)
    {
        Send(hwnd, SCI_BEGINUNDOACTION, 0, 0);
        try
        {
            Send(hwnd, SCI_SETTARGETSTART, (nuint)start, 0);
            Send(hwnd, SCI_SETTARGETEND, (nuint)end, 0);
            fixed (byte* ptr = utf8)
            {
                Send(hwnd, SCI_REPLACETARGET, (nuint)utf8.Length, utf8.Length == 0 ? 0 : (nint)ptr);
            }
            SetSelection(hwnd, start, start + utf8.Length, false);
        }
        finally
        {
            Send(hwnd, SCI_ENDUNDOACTION, 0, 0);
        }
    }

    public static (int XOffset, int FirstVisibleLine) GetScroll(nint hwnd)
    {
        int x = (int)Send(hwnd, SCI_GETXOFFSET, 0, 0);
        int line = (int)Send(hwnd, SCI_GETFIRSTVISIBLELINE, 0, 0);
        return (x, line);
    }

    public static void SetScroll(nint hwnd, int xOffset, int firstVisibleLine)
    {
        Send(hwnd, SCI_SETXOFFSET, (nuint)Math.Max(0, xOffset), 0);
        Send(hwnd, SCI_SETFIRSTVISIBLELINE, (nuint)Math.Max(0, firstVisibleLine), 0);
    }

    public static void ApplyLanguage(nint hwnd, string? filePath, int fontSizePt)
    {
        var ext = string.IsNullOrEmpty(filePath)
            ? "txt"
            : Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

        var (lexerName, kind) = ext switch
        {
            "rs" => ("rust", LexerKind.Rust),
            "cs" or "csx" => ("cpp", LexerKind.CSharp),
            "c" or "h" or "cpp" or "hpp" or "cc" or "cxx" => ("cpp", LexerKind.Cpp),
            "js" or "mjs" or "cjs" => ("cpp", LexerKind.Js),
            "ts" or "tsx" or "jsx" => ("cpp", LexerKind.Js),
            "json" or "jsonl" or "jsonc" => ("json", LexerKind.Json),
            "py" or "pyw" => ("python", LexerKind.Python),
            "ps1" or "psm1" or "psd1" => ("powershell", LexerKind.PowerShell),
            "xml" or "xaml" or "csproj" or "props" or "targets" or "config" or "svg" => ("xml", LexerKind.Xml),
            "html" or "htm" => ("hypertext", LexerKind.Xml),
            "css" => ("css", LexerKind.Css),
            "md" or "markdown" => ("markdown", LexerKind.Markdown),
            "yaml" or "yml" => ("yaml", LexerKind.Yaml),
            "toml" => ("props", LexerKind.Props),
            "ini" or "cfg" or "conf" => ("props", LexerKind.Props),
            "sql" => ("sql", LexerKind.Sql),
            "sh" or "bash" or "zsh" => ("bash", LexerKind.Bash),
            _ => ("", LexerKind.None),
        };

        ApplyFontAndTheme(hwnd, fontSizePt);

        if (kind == LexerKind.None || string.IsNullOrEmpty(lexerName))
        {
            Send(hwnd, SCI_SETILEXER, 0, 0);
            return;
        }

        EnsureLexillaLoaded();
        var lexerBytes = Encoding.ASCII.GetBytes(lexerName + "\0");
        nint lexer;
        fixed (byte* ptr = lexerBytes)
        {
            lexer = CreateLexerNative(ptr);
        }

        if (lexer == 0)
        {
            Send(hwnd, SCI_SETILEXER, 0, 0);
            return;
        }

        Send(hwnd, SCI_SETILEXER, 0, lexer);
        ApplyLexerPalette(hwnd, kind);
        Send(hwnd, SCI_COLOURISE, 0, -1);
    }

    private static void ApplyLexerPalette(nint hwnd, LexerKind kind)
    {
        uint comment = ThemePalette.Rgb(118, 136, 158);
        uint str = ThemePalette.Rgb(166, 214, 146);
        uint keyword = ThemePalette.Rgb(129, 178, 245);
        uint typeOrMacro = ThemePalette.Rgb(236, 189, 120);
        uint number = ThemePalette.Rgb(225, 160, 114);
        uint op = ThemePalette.Rgb(169, 191, 214);
        uint prop = ThemePalette.Rgb(136, 206, 224);
        uint error = ThemePalette.Rgb(255, 138, 128);

        switch (kind)
        {
            case LexerKind.Rust:
                SetKeywords(hwnd, 0, "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while");
                SetKeywords(hwnd, 1, "bool char f32 f64 i8 i16 i32 i64 i128 isize str u8 u16 u32 u64 u128 usize String Vec Option Result Box Arc Rc");
                foreach (int s in new[] { 1, 2, 3, 4 }) SetStyleFore(hwnd, s, comment);
                SetStyleFore(hwnd, 5, number);
                SetStyleFore(hwnd, 6, keyword);
                SetStyleFore(hwnd, 7, typeOrMacro);
                foreach (int s in new[] { 13, 14, 15, 21, 22, 23, 24, 25 }) SetStyleFore(hwnd, s, str);
                SetStyleFore(hwnd, 16, op);
                SetStyleFore(hwnd, 18, prop);
                SetStyleFore(hwnd, 19, typeOrMacro);
                SetStyleFore(hwnd, 20, error);
                break;

            case LexerKind.CSharp:
            case LexerKind.Cpp:
            case LexerKind.Js:
                if (kind == LexerKind.CSharp)
                {
                    SetKeywords(hwnd, 0, "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while async await var record required file init with and or not");
                }
                else if (kind == LexerKind.Js)
                {
                    SetKeywords(hwnd, 0, "async await break case catch class const continue debugger default delete do else export extends false finally for function if import in instanceof let new null return super switch this throw true try typeof var void while with yield interface type implements enum");
                }
                else
                {
                    SetKeywords(hwnd, 0, "alignas alignof auto bool break case catch char class const constexpr continue default delete do double else enum explicit export extern false float for friend goto if inline int long mutable namespace new noexcept nullptr operator private protected public register reinterpret_cast return short signed sizeof static static_assert struct switch template this throw true try typedef typeid typename union unsigned using virtual void volatile while");
                }
                foreach (int s in new[] { 1, 2, 3, 15, 17, 18, 24 }) SetStyleFore(hwnd, s, comment);
                SetStyleFore(hwnd, 4, number);
                SetStyleFore(hwnd, 5, keyword);
                foreach (int s in new[] { 6, 7, 12, 20, 22 }) SetStyleFore(hwnd, s, str);
                SetStyleFore(hwnd, 9, typeOrMacro);
                SetStyleFore(hwnd, 10, op);
                SetStyleFore(hwnd, 16, typeOrMacro);
                break;

            case LexerKind.Json:
                SetStyleFore(hwnd, 1, number);
                SetStyleFore(hwnd, 2, str);
                SetStyleFore(hwnd, 3, error);
                SetStyleFore(hwnd, 4, prop);
                SetStyleFore(hwnd, 5, typeOrMacro);
                SetStyleFore(hwnd, 6, comment);
                SetStyleFore(hwnd, 7, comment);
                SetStyleFore(hwnd, 8, op);
                SetStyleFore(hwnd, 11, keyword);
                SetStyleFore(hwnd, 13, error);
                break;

            case LexerKind.Python:
                SetKeywords(hwnd, 0, "and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield");
                SetStyleFore(hwnd, 1, comment);
                SetStyleFore(hwnd, 2, number);
                foreach (int s in new[] { 3, 4, 6, 7 }) SetStyleFore(hwnd, s, str);
                SetStyleFore(hwnd, 5, keyword);
                SetStyleFore(hwnd, 8, typeOrMacro);
                SetStyleFore(hwnd, 9, prop);
                SetStyleFore(hwnd, 10, op);
                SetStyleFore(hwnd, 12, comment);
                SetStyleFore(hwnd, 14, typeOrMacro);
                break;

            case LexerKind.PowerShell:
                SetKeywords(hwnd, 0, "begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach from function if in local param private process return switch throw trap try until using var while");
                foreach (int s in new[] { 1, 13 }) SetStyleFore(hwnd, s, comment);
                foreach (int s in new[] { 2, 3 }) SetStyleFore(hwnd, s, str);
                SetStyleFore(hwnd, 4, number);
                SetStyleFore(hwnd, 5, prop);
                SetStyleFore(hwnd, 6, op);
                SetStyleFore(hwnd, 8, keyword);
                SetStyleFore(hwnd, 9, typeOrMacro);
                break;

            case LexerKind.Xml:
                foreach (int s in new[] { 1, 11 }) SetStyleFore(hwnd, s, keyword);
                SetStyleFore(hwnd, 3, prop);
                foreach (int s in new[] { 6, 7 }) SetStyleFore(hwnd, s, str);
                SetStyleFore(hwnd, 9, comment);
                SetStyleFore(hwnd, 17, typeOrMacro);
                break;

            case LexerKind.Markdown:
                foreach (int s in new[] { 2, 3, 6, 7, 8, 9, 10, 11 }) SetStyleFore(hwnd, s, keyword);
                SetStyleFore(hwnd, 14, prop);
                SetStyleFore(hwnd, 18, typeOrMacro);
                SetStyleFore(hwnd, 19, str);
                SetStyleFore(hwnd, 20, str);
                break;

            case LexerKind.Yaml:
                SetStyleFore(hwnd, 1, comment);
                SetStyleFore(hwnd, 2, prop);
                SetStyleFore(hwnd, 3, keyword);
                SetStyleFore(hwnd, 4, number);
                SetStyleFore(hwnd, 6, str);
                SetStyleFore(hwnd, 7, str);
                SetStyleFore(hwnd, 9, op);
                break;

            case LexerKind.Props:
                SetStyleFore(hwnd, 1, comment);
                SetStyleFore(hwnd, 2, keyword);
                SetStyleFore(hwnd, 3, op);
                SetStyleFore(hwnd, 5, prop);
                break;

            case LexerKind.Sql:
                SetKeywords(hwnd, 0, "select from where join inner left right on group by order having insert into values update set delete create table alter drop index view as and or not null is in like distinct limit offset union all case when then else end");
                foreach (int s in new[] { 1, 2, 3 }) SetStyleFore(hwnd, s, comment);
                SetStyleFore(hwnd, 4, number);
                SetStyleFore(hwnd, 5, keyword);
                foreach (int s in new[] { 6, 7 }) SetStyleFore(hwnd, s, str);
                SetStyleFore(hwnd, 10, op);
                break;

            case LexerKind.Bash:
                SetKeywords(hwnd, 0, "if then else elif fi case esac for while until do done in function select time coproc export local readonly declare typeset unset return exit break continue");
                SetStyleFore(hwnd, 2, comment);
                SetStyleFore(hwnd, 3, number);
                SetStyleFore(hwnd, 4, keyword);
                foreach (int s in new[] { 5, 6 }) SetStyleFore(hwnd, s, str);
                SetStyleFore(hwnd, 7, op);
                SetStyleFore(hwnd, 9, prop);
                SetStyleFore(hwnd, 10, typeOrMacro);
                break;

            case LexerKind.Css:
                SetStyleFore(hwnd, 1, keyword);
                SetStyleFore(hwnd, 6, prop);
                SetStyleFore(hwnd, 9, comment);
                SetStyleFore(hwnd, 12, number);
                foreach (int s in new[] { 13, 14 }) SetStyleFore(hwnd, s, str);
                break;
        }
    }

    private static void SetStyleFore(nint hwnd, int style, uint color)
    {
        Send(hwnd, SCI_STYLESETFORE, (nuint)style, (nint)color);
    }

    private static void SetStyleFont(nint hwnd, int style, string fontName)
    {
        var bytes = Encoding.UTF8.GetBytes(fontName + "\0");
        fixed (byte* ptr = bytes)
        {
            Send(hwnd, SCI_STYLESETFONT, (nuint)style, (nint)ptr);
        }
    }

    private static void SetKeywords(nint hwnd, int listIndex, string words)
    {
        var bytes = Encoding.ASCII.GetBytes(words + "\0");
        fixed (byte* ptr = bytes)
        {
            Send(hwnd, SCI_SETKEYWORDS, (nuint)listIndex, (nint)ptr);
        }
    }

    private static nint Send(nint hwnd, uint msg, nuint wParam, nint lParam) =>
        Win32.SendMessageW(hwnd, msg, wParam, lParam);

    private enum LexerKind
    {
        None = 0,
        Rust = 1,
        CSharp = 2,
        Cpp = 3,
        Js = 4,
        Json = 5,
        Python = 6,
        PowerShell = 7,
        Xml = 8,
        Markdown = 9,
        Yaml = 10,
        Props = 11,
        Sql = 12,
        Bash = 13,
        Css = 14,
    }
}
