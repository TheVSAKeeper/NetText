namespace RustText.Demo.Editor
{
    internal readonly record struct CaretRange(int Anchor, int Caret);
}

namespace RustText.Demo.Logs
{
    internal static class LogLevels
    {
        internal static bool IsLogPath(string? path) =>
            string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase);
    }
}
