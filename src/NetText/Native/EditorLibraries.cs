using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace NetText.Native;

internal static partial class EditorLibraries
{
    private const uint SearchSystem32 = 0x00000800;

    public static nint Load(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        nint module = LoadLibraryExW(path, 0, SearchSystem32);
        if (module != 0) return module;
        throw new DllNotFoundException($"Не удалось загрузить «{path}».", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryExW(string name, nint file, uint flags);
}
