using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace NetText.Core;

internal sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string Url,
    [property: JsonPropertyName("size")] long Size = 0,
    [property: JsonPropertyName("digest")] string? Digest = null);

internal sealed record StableRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] ReleaseAsset[] Assets,
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("html_url")] string? HtmlUrl = null);

[JsonSerializable(typeof(StableRelease))]
[JsonSerializable(typeof(StableRelease[]))]
internal partial class ReleaseJson : JsonSerializerContext;

internal sealed record ReadyUpdate(string Stage, Version Version);

internal sealed record UpdateRelease(
    Version Version,
    string Tag,
    string HtmlUrl,
    string Body,
    string ArchiveUrl,
    string ChecksumUrl);

internal sealed record ReleaseNotesEntry(
    Version Version,
    string Tag,
    string HtmlUrl,
    string Body);

internal static partial class Updates
{
    private const string Releases = "https://api.github.com/repos/TheVSAKeeper/NetText/releases/latest";
    private const string ReleaseHistory = "https://api.github.com/repos/TheVSAKeeper/NetText/releases";
    private const string ReleasePagePrefix = "https://github.com/TheVSAKeeper/NetText/releases/tag/";
    private const string DownloadPrefix = "https://github.com/TheVSAKeeper/NetText/releases/download/";
    private const string ArchiveName = "NetText.zip";
    private const string Executable = "NetText.exe";
    private const string Helper = "NetText.Update.exe";
    private const string Checksum = ArchiveName + ".sha256";
    private const long ArchiveLimit = 128 * 1024 * 1024;
    private const int ReleaseHistoryPageSize = 100;
    private const int MaxReleaseHistoryPages = 100;
    private const string Scintilla = "Scintilla.dll";
    private const string Lexilla = "Lexilla.dll";
    private const string EditorLicense = "Scintilla-License.txt";
    private static readonly string[] LegacyBinaries = [Scintilla, Lexilla, EditorLicense, Executable];
    private static readonly string[] PackageFiles = [.. LegacyBinaries, "README.md", "docs/comparison.md", "docs/comparison-raw.json"];
    private static string UpdateRoot => Path.Combine(AppPaths.RequireLocalAppData(), "NetText", "updates");
    private static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static bool IsAllowedReleaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        string path = uri.AbsolutePath;
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }
        return path.StartsWith("/TheVSAKeeper/NetText/releases/tag/", StringComparison.Ordinal) ||
            path.StartsWith("/TheVSAKeeper/NetText/releases/download/", StringComparison.Ordinal);
    }

    public static async Task<UpdateRelease?> CheckLatestAsync(CancellationToken token)
    {
        using var client = CreateClient(TimeSpan.FromSeconds(25));
        return await CheckLatestAsync(client, token).ConfigureAwait(false);
    }

    internal static async Task<UpdateRelease?> CheckLatestAsync(HttpClient client, CancellationToken token)
    {
        var release = await client.GetFromJsonAsync(Releases, ReleaseJson.Default.StableRelease, token).ConfigureAwait(false);
        if (release is null || release.Draft || release.Prerelease || !TryReleaseVersion(release.Tag, out var version) || version <= CurrentVersion)
            return null;

        string archiveUrl = AssetUrl(release, ArchiveName);
        string checksumUrl = AssetUrl(release, Checksum);
        string htmlUrl = ValidateReleasePageUrl(release.HtmlUrl, release.Tag);
        return new UpdateRelease(version, release.Tag, htmlUrl, release.Body ?? "", archiveUrl, checksumUrl);
    }

    public static async Task<ReadyUpdate> StageAsync(UpdateRelease release, CancellationToken token)
    {
        using var client = CreateClient(TimeSpan.FromMinutes(2));
        return await StageAsync(release, client, token).ConfigureAwait(false);
    }

    internal static async Task<ReadyUpdate> StageAsync(UpdateRelease release, HttpClient client, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!IsAllowedReleaseUrl(release.ArchiveUrl) || !IsAllowedReleaseUrl(release.ChecksumUrl) || !IsAllowedReleaseUrl(release.HtmlUrl))
        {
            throw new InvalidDataException("Недопустимый URL обновления.");
        }

        string stage = Path.Combine(UpdateRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            string checksum = Path.Combine(stage, Checksum);
            string archive = Path.Combine(stage, ArchiveName);
            await Download(client, release.ChecksumUrl, checksum, 1024, token).ConfigureAwait(false);
            await Download(client, release.ArchiveUrl, archive, ArchiveLimit, token).ConfigureAwait(false);
            var binaries = VerifyAndStage(archive, File.ReadAllText(checksum), stage);
            VerifyBinaries(stage, binaries);
            return new ReadyUpdate(stage, release.Version);
        }
        catch
        {
            CleanupStage(stage);
            throw;
        }
    }

    public static async Task<IReadOnlyList<ReleaseNotesEntry>> GetReleaseHistoryAsync(CancellationToken token)
    {
        using var client = CreateClient(TimeSpan.FromSeconds(30));
        return await GetReleaseHistoryAsync(client, token).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<ReleaseNotesEntry>> GetReleaseHistoryAsync(HttpClient client, CancellationToken token)
    {
        var entries = new List<ReleaseNotesEntry>();
        for (int page = 1; page <= MaxReleaseHistoryPages; page++)
        {
            string url = $"{ReleaseHistory}?per_page={ReleaseHistoryPageSize}&page={page}";
            var batch = await client.GetFromJsonAsync(url, ReleaseJson.Default.StableReleaseArray, token).ConfigureAwait(false);
            if (batch is null || batch.Length == 0)
            {
                return entries;
            }

            foreach (var release in batch)
            {
                if (release.Draft || release.Prerelease || !TryReleaseVersion(release.Tag, out var version))
                {
                    continue;
                }
                string htmlUrl = ValidateReleasePageUrl(release.HtmlUrl, release.Tag);
                entries.Add(new ReleaseNotesEntry(version, release.Tag, htmlUrl, release.Body ?? ""));
            }

            if (batch.Length < ReleaseHistoryPageSize)
            {
                return entries;
            }
        }

        throw new InvalidDataException("История выпусков GitHub превышает предел пагинации.");
    }

    public static async Task<ReadyUpdate?> CheckAsync(CancellationToken token)
    {
        var installation = Environment.ProcessPath;
        if (installation is null || !string.Equals(Path.GetFileName(installation), Executable, StringComparison.OrdinalIgnoreCase)) return null;
        using var client = CreateClient(TimeSpan.FromSeconds(25));
        var release = await client.GetFromJsonAsync(Releases, ReleaseJson.Default.StableRelease, token);
        if (release is null || release.Draft || release.Prerelease || !TryReleaseVersion(release.Tag, out var version) || version <= CurrentVersion)
            return null;

        string archiveUrl = AssetUrl(release, ArchiveName);
        string checksumUrl = AssetUrl(release, Checksum);
        string stage = Path.Combine(UpdateRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            string checksum = Path.Combine(stage, Checksum);
            string archive = Path.Combine(stage, ArchiveName);
            await Download(client, checksumUrl, checksum, 1024, token);
            await Download(client, archiveUrl, archive, ArchiveLimit, token);
            var binaries = VerifyAndStage(archive, File.ReadAllText(checksum), stage);
            VerifyBinaries(stage, binaries);
            return new ReadyUpdate(stage, version);
        }
        catch
        {
            CleanupStage(stage);
            throw;
        }
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"NetText/{CurrentVersion}");
        return client;
    }

    private static string ValidateReleasePageUrl(string? htmlUrl, string tag)
    {
        string expected = ReleasePagePrefix + tag;
        if (string.IsNullOrEmpty(htmlUrl))
        {
            return expected;
        }
        return string.Equals(htmlUrl, expected, StringComparison.Ordinal)
            ? expected
            : throw new InvalidDataException("Страница выпуска указывает вне репозитория NetText.");
    }

    private static string AssetUrl(StableRelease release, string name)
    {
        var asset = release.Assets.SingleOrDefault(item => item.Name == name);
        string expected = DownloadPrefix + release.Tag + "/" + name;
        return asset?.Url == expected ? expected : throw new InvalidDataException($"В выпуске отсутствует {name}.");
    }

    internal static bool TryReleaseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (tag.Length < 2 || tag[0] != 'v' || !Version.TryParse(tag[1..], out var result) ||
            result.Build < 0 || result.Revision >= 0 || tag != $"v{result}") return false;
        version = new Version(result.Major, result.Minor, result.Build, 0);
        return true;
    }

    private static async Task Download(HttpClient client, string url, string path, long limit, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Файл выпуска слишком велик.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[65536];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            written += read;
            if (written > limit) throw new InvalidDataException("Файл выпуска слишком велик.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        output.Flush(flushToDisk: true);
    }

    internal static string[] VerifyAndStage(string archive, string checksum, string stage)
    {
        string[] parts = checksum.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[1] != ArchiveName || parts[0].Length != 64 ||
            !parts[0].All(Uri.IsHexDigit) || !SameHash(archive, parts[0]))
            throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
        using var zip = ZipFile.OpenRead(archive);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!PackageFiles.Contains(entry.FullName) || !names.Add(entry.FullName) || entry.Length <= 0 || entry.Length > ArchiveLimit)
                throw new InvalidDataException("Некорректный состав архива обновления.");
        }
        bool legacy = names.Contains(Scintilla) || names.Contains(Lexilla);
        string[] binaries = legacy ? LegacyBinaries : [Executable];
        if (binaries.Any(name => !names.Contains(name))) throw new InvalidDataException("В архиве нет обязательных файлов NetText.");
        Directory.CreateDirectory(stage);
        foreach (string name in binaries)
        {
            string path = Path.Combine(stage, "new-" + name);
            zip.GetEntry(name)!.ExtractToFile(path, overwrite: true);
        }
        return binaries;
    }

    private static bool SameHash(string path, string hash)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private static void VerifyBinaries(string stage, string[] binaries)
    {
        foreach (string name in binaries.Where(name => !name.EndsWith(".txt", StringComparison.Ordinal)))
        {
            using var input = File.OpenRead(Path.Combine(stage, "new-" + name));
            using var pe = new PEReader(input);
            if (pe.PEHeaders.PEHeader is null || pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 ||
                name.EndsWith(".dll", StringComparison.Ordinal) != pe.PEHeaders.IsDll)
                throw new InvalidDataException($"Некорректный бинарный файл выпуска: {name}.");
        }
    }

    public static void Schedule(ReadyUpdate update)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Путь NetText недоступен.");
        string helper = Path.Combine(update.Stage, Helper);
        File.Copy(executable, helper, overwrite: true);
        using var current = Process.GetCurrentProcess();
        var startInfo = new ProcessStartInfo(helper) { CreateNoWindow = true, UseShellExecute = false };
        startInfo.ArgumentList.Add("--install-update");
        startInfo.ArgumentList.Add(executable);
        startInfo.ArgumentList.Add(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(startInfo) ?? throw new IOException("Не удалось запустить установщик обновления.");
    }

    public static int Install(string installation, string processId, string started)
    {
        SetErrorMode(0x8003);
        string stage = "";
        string? backupDirectory = null;
        var installed = new List<string>();
        try
        {
            string self = Environment.ProcessPath ?? throw new IOException("Нет пути к установщику.");
            stage = Path.GetDirectoryName(self)!;
            if (!IsStage(stage) || Path.GetFileName(self) != Helper || !Path.IsPathFullyQualified(installation) ||
                !string.Equals(Path.GetFileName(installation), Executable, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(processId, out int id) || !long.TryParse(started, out long startTicks)) return 1;
            Process? parent;
            try { parent = Process.GetProcessById(id); }
            catch (ArgumentException) { parent = null; }
            using (parent)
            {
                if (parent is not null && parent.StartTime.ToUniversalTime().Ticks == startTicks &&
                    !parent.WaitForExit(30000)) return 1;
            }
            using (var file = File.OpenRead(self))
                if (!SameHash(installation, Convert.ToHexString(SHA256.HashData(file))))
                    throw new IOException("Установленный NetText изменился после загрузки обновления.");

            var binaries = VerifyAndStage(Path.Combine(stage, ArchiveName), File.ReadAllText(Path.Combine(stage, Checksum)), stage);
            VerifyBinaries(stage, binaries);
            string directory = Path.GetDirectoryName(installation)!;
            backupDirectory = Path.Combine(directory, ".nettext-update-" + Path.GetFileName(stage));
            Directory.CreateDirectory(backupDirectory);
            foreach (string name in binaries)
            {
                string result = Path.Combine(directory, name);
                string pending = Path.Combine(backupDirectory, "new-" + name);
                File.Copy(Path.Combine(stage, "new-" + name), pending);
                if (File.Exists(result)) File.Replace(pending, result, Path.Combine(backupDirectory, name));
                else File.Move(pending, result);
                installed.Add(name);
            }

            var launch = new ProcessStartInfo(installation) { UseShellExecute = false };
            launch.ArgumentList.Add("--update-ready");
            launch.ArgumentList.Add(Path.GetFileName(stage));
            string ready = Path.Combine(stage, "ready");
            using (var child = Process.Start(launch) ?? throw new IOException("Обновлённый NetText не запустился."))
            {
                for (int poll = 0; poll < 200 && !child.HasExited && !File.Exists(ready); poll++) Thread.Sleep(100);
                if (!File.Exists(ready))
                {
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
                    throw new IOException("Обновлённый NetText не подтвердил запуск.");
                }
            }
            try { Directory.Delete(backupDirectory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return 0;
        }
        catch (Exception failure)
        {
            if (!IsStage(stage)) return 1;
            var failures = new List<string> { failure.Message };
            if (backupDirectory is not null)
            {
                string directory = Path.GetDirectoryName(installation)!;
                foreach (string name in installed.AsEnumerable().Reverse())
                {
                    try
                    {
                        string backup = Path.Combine(backupDirectory, name);
                        string destination = Path.Combine(directory, name);
                        if (File.Exists(backup)) File.Move(backup, destination, overwrite: true);
                        else File.Delete(destination);
                    }
                    catch (Exception error) { failures.Add(error.Message); }
                }
                if (failures.Count == 1)
                {
                    try { Directory.Delete(backupDirectory, recursive: true); }
                    catch (IOException error) { failures.Add(error.Message); }
                }
            }
            try { File.WriteAllLines(Path.Combine(stage, "update-error.txt"), failures); }
            catch (IOException) { }
            try
            {
                var fallback = new ProcessStartInfo(installation) { UseShellExecute = false };
                fallback.ArgumentList.Add("--update-failed");
                fallback.ArgumentList.Add(Path.GetFileName(stage));
                Process.Start(fallback)?.Dispose();
            }
            catch (Exception) { }
            return 1;
        }
    }

    public static void Acknowledge(string stageId)
    {
        if (Guid.TryParseExact(stageId, "N", out _) && Directory.Exists(Path.Combine(UpdateRoot, stageId)))
            File.WriteAllText(Path.Combine(UpdateRoot, stageId, "ready"), "");
    }

    public static string? ReadFailure(string stageId)
    {
        if (!Guid.TryParseExact(stageId, "N", out _)) return null;
        string path = Path.Combine(UpdateRoot, stageId, "update-error.txt");
        try { return File.ReadLines(path).FirstOrDefault(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void CleanupStage(string path)
    {
        if (!IsStage(path) || !Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static void CleanupCompleted()
    {
        try
        {
            if (!Directory.Exists(UpdateRoot)) return;
            foreach (string directory in Directory.EnumerateDirectories(UpdateRoot))
            {
                var ready = Path.Combine(directory, "ready");
                if (File.Exists(ready) && File.GetLastWriteTimeUtc(ready) < DateTime.UtcNow.AddHours(-1))
                    CleanupStage(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsStage(string path) => !string.IsNullOrEmpty(path) && Guid.TryParseExact(Path.GetFileName(path), "N", out _) &&
        string.Equals(Path.GetDirectoryName(path), UpdateRoot, StringComparison.OrdinalIgnoreCase);

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);
}
