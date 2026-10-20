using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinecraftDownloader.Core.Models;

namespace MinecraftDownloader.Core;

public static class VersionService
{
    private static readonly SemaphoreSlim ManifestLock = new(1, 1);
    private static VersionManifest? _manifest;

    public static async Task<VersionManifest> GetManifestAsync(bool force = false,
        CancellationToken ct = default)
    {
        await ManifestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cacheFile = Path.Combine(AppPaths.CacheDir, "version_manifest_v2.json");
            if (!force && _manifest is not null && _manifest.Versions.Count > 0)
                return _manifest;

            if (!force && File.Exists(cacheFile) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < TimeSpan.FromHours(6))
            {
                try
                {
                    _manifest = JsonSerializer.Deserialize<VersionManifest>(
                        await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false), JsonDefaults.Options);
                    if (_manifest is { Versions.Count: > 0 })
                        return _manifest;
                }
                catch
                {
                    // 缓存损坏则重新下载
                }
            }

            var json = await HttpService
                .GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct)
                .ConfigureAwait(false);
            _manifest = JsonSerializer.Deserialize<VersionManifest>(json, JsonDefaults.Options)
                        ?? new VersionManifest();
            Directory.CreateDirectory(AppPaths.CacheDir);
            await File.WriteAllTextAsync(cacheFile, json, ct).ConfigureAwait(false);
            return _manifest;
        }
        finally
        {
            ManifestLock.Release();
        }
    }

    public static async Task<VersionMeta> FetchMetaAsync(string url, CancellationToken ct = default)
    {
        var json = await HttpService.GetStringAsync(url, ct).ConfigureAwait(false);
        return ParseMeta(json);
    }

    public static VersionMeta ParseMeta(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var meta = new VersionMeta
        {
            Id = node["id"]?.GetValue<string>() ?? "",
            Type = node["type"]?.GetValue<string>(),
            MainClass = node["mainClass"]?.GetValue<string>(),
            Assets = node["assets"]?.GetValue<string>(),
            InheritsFrom = node["inheritsFrom"]?.GetValue<string>(),
            MinecraftArguments = node["minecraftArguments"]?.GetValue<string>(),
            AssetIndex = node["assetIndex"]?.Deserialize<AssetIndexInfo>(JsonDefaults.Options),
            Downloads = node["downloads"]?.Deserialize<VersionDownloads>(JsonDefaults.Options),
            Libraries = node["libraries"]?.Deserialize<List<LibraryInfo>>(JsonDefaults.Options) ?? new(),
            JavaVersion = node["javaVersion"]?.Deserialize<JavaVersionInfo>(JsonDefaults.Options),
            Logging = node["logging"]?.Deserialize<LoggingInfo>(JsonDefaults.Options),
            RawJson = json
        };

        if (node["arguments"] is JsonObject arguments)
        {
            meta.GameArguments = GameArgument.Parse(arguments["game"]);
            meta.JvmArguments = GameArgument.Parse(arguments["jvm"]);
        }

        return meta;
    }

    public static string GetJsonPath(string id) => Path.Combine(AppPaths.GetInstanceDir(id), id + ".json");

    public static VersionMeta? LoadLocalMeta(string id)
    {
        var path = GetJsonPath(id);
        if (!File.Exists(path))
            return null;
        try
        {
            var meta = ParseMeta(File.ReadAllText(path));
            if (string.IsNullOrEmpty(meta.Id))
                meta.Id = id;
            return meta;
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取版本 {id} 的 JSON 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>按 inheritsFrom 链合并出完整的版本信息。</summary>
    public static VersionMeta Resolve(VersionMeta meta)
    {
        var chain = new List<VersionMeta> { meta };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { meta.Id };
        var current = meta;

        while (!string.IsNullOrEmpty(current.InheritsFrom) && chain.Count < 8)
        {
            var parentId = current.InheritsFrom!;
            if (!seen.Add(parentId))
                break;
            var parent = LoadLocalMeta(parentId);
            if (parent is null)
                break;
            chain.Add(parent);
            current = parent;
        }

        var resolved = new VersionMeta
        {
            Id = meta.Id,
            Type = meta.Type ?? chain.Select(x => x.Type).FirstOrDefault(x => !string.IsNullOrEmpty(x)),
            MainClass = meta.MainClass ?? chain.Select(x => x.MainClass).FirstOrDefault(x => !string.IsNullOrEmpty(x)),
            Assets = meta.Assets ?? chain.Select(x => x.Assets).FirstOrDefault(x => !string.IsNullOrEmpty(x)),
            InheritsFrom = meta.InheritsFrom,
            AssetIndex = meta.AssetIndex ?? chain.Select(x => x.AssetIndex).FirstOrDefault(x => x is not null),
            Downloads = meta.Downloads ?? chain.Select(x => x.Downloads).FirstOrDefault(x => x is not null),
            JavaVersion = meta.JavaVersion ?? chain.Select(x => x.JavaVersion).FirstOrDefault(x => x is not null),
            Logging = meta.Logging ?? chain.Select(x => x.Logging).FirstOrDefault(x => x is not null),
            MinecraftArguments = meta.MinecraftArguments ??
                                 chain.Select(x => x.MinecraftArguments).FirstOrDefault(x => !string.IsNullOrEmpty(x)),
            RawJson = meta.RawJson
        };

        var libraryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in chain)
        {
            foreach (var library in item.Libraries)
            {
                if (libraryNames.Add(library.Name))
                    resolved.Libraries.Add(library);
            }
        }

        foreach (var item in chain)
        {
            resolved.GameArguments.AddRange(item.GameArguments);
            resolved.JvmArguments.AddRange(item.JvmArguments);
        }

        return resolved;
    }

    public static bool IsWindows()
        => OperatingSystem.IsWindows();

    public static bool IsRuleAllowed(List<Rule>? rules, Dictionary<string, bool>? features = null)
    {
        if (rules is null || rules.Count == 0)
            return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!RuleMatches(rule, features))
                continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool RuleMatches(Rule rule, Dictionary<string, bool>? features)
    {
        if (rule.Os is not null)
        {
            var osName = rule.Os.Name;
            if (!string.IsNullOrEmpty(osName))
            {
                var match = osName.ToLowerInvariant() switch
                {
                    "windows" => OperatingSystem.IsWindows(),
                    "osx" => OperatingSystem.IsMacOS(),
                    "linux" => OperatingSystem.IsLinux(),
                    _ => false
                };
                if (!match)
                    return false;
            }

            if (!string.IsNullOrEmpty(rule.Os.Arch))
            {
                var arch = rule.Os.Arch.ToLowerInvariant();
                var is64 = Environment.Is64BitOperatingSystem;
                var match = arch switch
                {
                    "x86" => !Environment.Is64BitProcess,
                    "x86_64" or "amd64" => Environment.Is64BitProcess,
                    "arm64" or "aarch64" => System.Runtime.InteropServices.RuntimeInformation
                        .OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64,
                    _ => true
                };
                if (!match)
                    return false;
            }
        }

        if (rule.Features is { Count: > 0 })
        {
            foreach (var (key, expected) in rule.Features)
            {
                if (features is not null && features.TryGetValue(key, out var actual))
                {
                    if (actual != expected)
                        return false;
                }
                else if (expected)
                {
                    // 未启用的可选特性视为 false
                    return false;
                }
            }
        }

        return true;
    }

    public static IEnumerable<LibraryInfo> GetEffectiveLibraries(VersionMeta meta)
        => meta.Libraries.Where(x => IsRuleAllowed(x.Rules));

    public static string GetLibraryRelativePath(LibraryInfo library)
    {
        var artifactPath = library.Downloads?.Artifact?.Path;
        if (!string.IsNullOrEmpty(artifactPath))
            return artifactPath;

        var parts = library.Name.Split(':');
        if (parts.Length < 3)
            return library.Name.Replace(':', '/');
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    public static IEnumerable<DownloadItem> GetLibraryDownloads(VersionMeta meta)
    {
        foreach (var library in GetEffectiveLibraries(meta))
        {
            var artifact = library.Downloads?.Artifact;
            if (artifact?.Url is not null)
            {
                var path = artifact.Path ?? GetLibraryRelativePath(library);
                yield return new DownloadItem(artifact.Url,
                    Path.Combine("libraries", path.Replace('/', Path.DirectorySeparatorChar)),
                    artifact.Sha1, artifact.Size);
            }

            if (library.Natives is null)
                continue;
            var classifierKey = library.Natives.TryGetValue("windows", out var key) ? key : null;
            if (classifierKey is null)
                continue;
            var nativeArtifact = library.Downloads?.Classifiers?.GetValueOrDefault(
                classifierKey.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32"));
            if (nativeArtifact?.Url is null)
                continue;
            var nativePath = nativeArtifact.Path ?? $"{GetLibraryRelativePath(library)[..^4]}-{classifierKey}.jar";
            yield return new DownloadItem(nativeArtifact.Url,
                Path.Combine("libraries", nativePath.Replace('/', Path.DirectorySeparatorChar)),
                nativeArtifact.Sha1, nativeArtifact.Size);
        }
    }

    public static async Task DownloadLibrariesAsync(VersionMeta meta, IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var items = GetLibraryDownloads(meta).ToList();
        if (items.Count == 0)
            return;
        var reporter = new Progress<DownloadReport>(report => progress?.Report(new InstallProgress
        {
            Stage = "下载依赖库",
            Detail = report.CurrentFile,
            Percent = report.Percent,
            DoneBytes = report.CompletedBytes,
            TotalBytes = report.TotalBytes
        }));
        await DownloadService.DownloadAllAsync(items, reporter, ct).ConfigureAwait(false);
    }

    public static async Task DownloadClientAsync(VersionMeta meta, IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var client = meta.Downloads?.Client;
        if (client?.Url is null)
            return;
        var target = Path.Combine(AppPaths.GetInstanceDir(meta.Id), meta.Id + ".jar");
        if (File.Exists(target) && client.Size > 0 && new FileInfo(target).Length == client.Size)
            return;

        var fileProgress = new SynchronousProgress<(long Done, long Total)>(p => progress?.Report(
            new InstallProgress
            {
                Stage = "下载游戏本体",
                Detail = $"{meta.Id}.jar  {p.Done / 1048576.0:0.0} / {p.Total / 1048576.0:0.0} MB",
                Percent = p.Total > 0 ? p.Done * 100.0 / p.Total : null
            }));

        await HttpService.DownloadFileAsync(client.Url, target, client.Sha1, fileProgress, ct)
            .ConfigureAwait(false);
    }
}
