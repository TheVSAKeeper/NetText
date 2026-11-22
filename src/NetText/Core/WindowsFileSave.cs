using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace NetText.Core;

internal static unsafe partial class WindowsFileSave
{
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const int FileRenameInfo = 3;

    public static FileStream CreatePending(string path)
    {
        using var user = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
            65536, FileOptions.WriteThrough, security);
    }

    public static void Replace(string pending, string path, FileVersion version)
    {
        using var handle = CreateFileW(ExtendedPath(path), GenericRead | DeleteAccess, (uint)FileShare.Read,
            0, (uint)FileMode.Open, 0, 0);
        if (handle.IsInvalid) throw Win32Error($"Не удалось защитить файл для сохранения «{path}»");
        using var guard = new FileStream(handle, FileAccess.Read);
        if (guard.Length != version.Bytes || File.GetLastWriteTimeUtc(handle) != version.ModifiedUtc ||
            File.GetCreationTimeUtc(handle) != version.CreatedUtc)
            throw new IOException("Файл изменился на диске. Откройте его заново или сохраните правки в отдельный файл.");

        CopyStreams(path, pending);
        File.SetCreationTimeUtc(pending, version.CreatedUtc);
        new FileInfo(pending).SetAccessControl(guard.GetAccessControl());

        string backup = Path.Combine(Path.GetDirectoryName(path)!, $".nettext-original-{Guid.NewGuid():N}.bak");
        Rename(handle, backup);
        try
        {
            File.Move(pending, path, overwrite: false);
        }
        catch (Exception error)
        {
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException($"Новый файл по этому пути появился во время сохранения. Исходный файл сохранён в «{backup}».", error);
            try { Rename(handle, path); }
            catch (IOException restore)
            {
                throw new IOException($"Не удалось восстановить исходный путь. Исходный файл сохранён в «{backup}». {restore.Message}", error);
            }
            throw;
        }
        guard.Dispose();
        try { File.Delete(backup); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void CopyStreams(string path, string pending)
    {
        StreamData data;
        nint find = FindFirstStreamW(ExtendedPath(path), 0, &data, 0);
        if (find == -1)
        {
            if (Marshal.GetLastPInvokeError() is 1 or 38 or 50 or 87) return;
            throw Win32Error("Не удалось прочитать потоки файла");
        }
        try
        {
            do
            {
                string stream = new(data.Name);
                if (stream == "::$DATA") continue;
                using var source = new FileStream(ExtendedPath(path) + stream, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var destination = new FileStream(ExtendedPath(pending) + stream, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            } while (FindNextStreamW(find, &data));
            if (Marshal.GetLastPInvokeError() != 38) throw Win32Error("Не удалось прочитать потоки файла");
        }
        finally { FindClose(find); }
    }

    private static string ExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + Path.GetFullPath(path);
    }

    private static void Rename(SafeFileHandle handle, string path)
    {
        string fullPath = ExtendedPath(path);
        int nameOffset = 2 * IntPtr.Size + sizeof(int);
        int nameBytes = checked(fullPath.Length * sizeof(char));
        var buffer = new byte[nameOffset + nameBytes + sizeof(char)];
        fixed (byte* address = buffer)
        {
            *(int*)(address + nameOffset - sizeof(int)) = nameBytes;
            fullPath.AsSpan().CopyTo(new Span<char>(address + nameOffset, fullPath.Length));
            if (!SetFileInformationByHandle(handle, FileRenameInfo, address, (uint)buffer.Length))
                throw Win32Error($"Не удалось переместить исходный файл в «{path}»");
        }
    }

    private static IOException Win32Error(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamData
    {
        public long Size;
        public fixed char Name[296];
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string file, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int kind, void* data, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindFirstStreamW(string path, int kind, StreamData* data, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextStreamW(nint find, StreamData* data);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint find);
}
