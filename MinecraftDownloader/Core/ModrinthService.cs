using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftDownloader.Core;

public sealed class ModrinthProject
{
    [JsonPropertyName("project_id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("icon_url")]
    public string IconUrl { get; set; } = "";

    [JsonPropertyName("project_type")]
    public string ProjectType { get; set; } = "";

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();

    [JsonPropertyName("versions")]
    public List<string> Versions { get; set; } = new();

    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; set; }

    [JsonIgnore]
    public string TitleOrSlug => string.IsNullOrEmpty(Title) ? Slug : Title;
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = "";

    [JsonPropertyName("version_type")]
    public string VersionType { get; set; } = "";

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = new();

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = new();

    [JsonPropertyName("date_published")]
    public DateTime? DatePublished { get; set; }

    [JsonPropertyName("files")]
    public List<ModrinthFile> Files { get; set; } = new();

    [JsonPropertyName("dependencies")]
    public List<ModrinthDependency> Dependencies { get; set; } = new();
}

public sealed class ModrinthFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("filename")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("hashes")]
    public Dictionary<string, string> Hashes { get; set; } = new();
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("version_id")]
    public string? VersionId { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("dependency_type")]
    public string? DependencyType { get; set; }
}

public static class ModrinthService
{
    private const string ApiBase = "https://api.modrinth.com/v2";

    private sealed class SearchResponse
    {
        [JsonPropertyName("hits")]
        public List<ModrinthProject> Hits { get; set; } = new();

        [JsonPropertyName("total_hits")]
        public long TotalHits { get; set; }
    }

    private sealed class HashLookupRequest
    {
        [JsonPropertyName("hashes")]
        public List<string> Hashes { get; set; } = new();

        [JsonPropertyName("algorithm")]
        public string Algorithm { get; set; } = "sha1";
    }

    public static async Task<List<ModrinthProject>> SearchAsync(string query, string projectType,
        string? gameVersion = null, string? loader = null, int limit = 20, int offset = 0,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);

        var facets = new List<List<string>>();
        if (!string.IsNullOrWhiteSpace(projectType))
            facets.Add(new List<string> { "project_type:" + projectType.Trim().ToLowerInvariant() });
        var normalizedLoader = NormalizeLoader(loader);
        if (normalizedLoader is not null)
            facets.Add(new List<string> { "categories:" + normalizedLoader });
        if (!string.IsNullOrWhiteSpace(gameVersion))
            facets.Add(new List<string> { "versions:" + gameVersion.Trim() });

        var url = $"{ApiBase}/search?query={Uri.EscapeDataString((query ?? "").Trim())}" +
                  $"&limit={limit}&offset={offset}&index=relevance" +
                  $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";

        try
        {
            var json = await HttpService.GetStringAsync(url, ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<SearchResponse>(json, JsonDefaults.Options);
            return response?.Hits ?? new List<ModrinthProject>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn($"Modrinth 搜索失败：{ex.Message}");
            return new List<ModrinthProject>();
        }
    }

    public static async Task<List<ModrinthVersion>> GetVersionsAsync(string projectId,
        string? gameVersion = null, string? loader = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return new List<ModrinthVersion>();

        var url = $"{ApiBase}/project/{Uri.EscapeDataString(projectId.Trim())}/version";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
            query.Add("game_versions=" + Uri.EscapeDataString(
                JsonSerializer.Serialize(new[] { gameVersion.Trim() })));
        var normalizedLoader = NormalizeLoader(loader);
        if (normalizedLoader is not null)
            query.Add("loaders=" + Uri.EscapeDataString(
                JsonSerializer.Serialize(new[] { normalizedLoader })));
        if (query.Count > 0)
            url += "?" + string.Join("&", query);

        try
        {
            var json = await HttpService.GetStringAsync(url, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<ModrinthVersion>>(json, JsonDefaults.Options)
                   ?? new List<ModrinthVersion>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn($"获取 Modrinth 版本列表失败：{ex.Message}");
            return new List<ModrinthVersion>();
        }
    }

    public static async Task<string> DownloadVersionAsync(ModrinthVersion version, string targetDirectory,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("目标目录为空。", nameof(targetDirectory));

        var files = version.Files ?? new List<ModrinthFile>();
        var file = files.FirstOrDefault(f => f.Primary) ?? files.FirstOrDefault();
        if (file is null)
            throw new InvalidOperationException(
                $"版本 {version.Name}（{version.VersionNumber}）没有可下载的文件。");
        if (string.IsNullOrWhiteSpace(file.Url))
            throw new InvalidOperationException(
                $"版本 {version.Name}（{version.VersionNumber}）的文件缺少下载地址。");

        var fileName = ResolveFileName(file, version);
        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, fileName);

        if (File.Exists(targetPath) &&
            (file.Size <= 0 || new FileInfo(targetPath).Length == file.Size))
        {
            progress?.Report(InstallProgress.Status("下载模组", fileName, 100));
            return targetPath;
        }

        var sha1 = file.Hashes is not null && file.Hashes.TryGetValue("sha1", out var hash) ? hash : null;
        var fileProgress = new SynchronousProgress<(long Done, long Total)>(p => progress?.Report(
            new InstallProgress
            {
                Stage = "下载模组",
                Detail = $"{fileName}  {p.Done / 1048576.0:0.0} / {p.Total / 1048576.0:0.0} MB",
                Percent = p.Total > 0 ? p.Done * 100.0 / p.Total : null,
                DoneBytes = p.Done,
                TotalBytes = p.Total
            }));

        await HttpService.DownloadFileAsync(file.Url, targetPath, sha1, fileProgress, ct)
            .ConfigureAwait(false);
        progress?.Report(InstallProgress.Status("下载模组", fileName, 100));
        return targetPath;
    }

    public static async Task<Dictionary<string, ModrinthVersion>> LookupBySha1Async(
        IEnumerable<string> sha1Hashes, CancellationToken ct = default)
    {
        var hashes = sha1Hashes is null
            ? new List<string>()
            : sha1Hashes.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();
        if (hashes.Count == 0)
            return new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);

        var body = JsonSerializer.Serialize(new HashLookupRequest { Hashes = hashes, Algorithm = "sha1" });
        try
        {
            var json = await HttpService.PostJsonAsync($"{ApiBase}/version_files", body, ct)
                .ConfigureAwait(false);
            var map = JsonSerializer.Deserialize<Dictionary<string, ModrinthVersion?>>(
                json, JsonDefaults.Options);
            var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
            if (map is not null)
            {
                foreach (var (hash, version) in map)
                {
                    if (version is not null)
                        result[hash] = version;
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn($"查询 Modrinth 文件信息失败：{ex.Message}");
            return new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ResolveFileName(ModrinthFile file, ModrinthVersion version)
    {
        var raw = file.FileName;
        if (string.IsNullOrWhiteSpace(raw))
            raw = UrlFileName(file.Url) ?? $"{version.ProjectId}-{version.Id}.jar";
        var name = Path.GetFileName(raw.Replace('\\', '/').Trim());
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        name = name.Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(name) ? "mod.jar" : name;
    }

    private static string? UrlFileName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeLoader(string? loader)
        => string.IsNullOrWhiteSpace(loader) ? null : loader.Trim().ToLowerInvariant();
}
