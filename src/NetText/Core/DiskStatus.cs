using System.IO;

namespace NetText.Core;

public enum DiskStatusKind
{
    None = 0,
    Current = 1,
    Changed = 2,
    Missing = 3,
    Unavailable = 4,
}

public static class DiskStatus
{
    public static DiskStatusKind FromException(Exception error) =>
        error is FileNotFoundException or DirectoryNotFoundException
            ? DiskStatusKind.Missing
            : DiskStatusKind.Unavailable;

    public static DiskStatusKind Check(FileTarget target)
    {
        try
        {
            var current = FileVersion.At(target.Path);
            if (current is null)
            {
                return DiskStatusKind.Missing;
            }

            return current == target.Version ? DiskStatusKind.Current : DiskStatusKind.Changed;
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    public static string? TabSuffix(DiskStatusKind status) => status switch
    {
        DiskStatusKind.Changed => "изменён на диске",
        DiskStatusKind.Missing => "отсутствует",
        DiskStatusKind.Unavailable => "недоступен",
        _ => null,
    };

    public static string? Notice(DiskStatusKind status) => status switch
    {
        DiskStatusKind.Changed => "Файл изменился на диске. Откройте его заново или сохраните правки отдельно.",
        DiskStatusKind.Missing => "Файл отсутствует на диске.",
        DiskStatusKind.Unavailable => "Файл недоступен. Проверьте путь и права доступа.",
        _ => null,
    };
}
