using System.IO;

namespace NetText.Core;

internal static class AppPaths
{
    public static string? GetLocalAppData()
    {
        var env = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(folder) ? null : folder;
    }

    public static string RequireLocalAppData() =>
        GetLocalAppData() ?? throw new IOException("Папка LOCALAPPDATA недоступна");
}
