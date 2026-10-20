using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MinecraftDownloader.Core.Models;

namespace MinecraftDownloader.Core;

public static class Installer
{
    private const string FabricLoaderApi = "https://meta.fabricmc.net/v2/versions/loader";
    private const string QuiltLoaderApi = "https://meta.quiltmc.org/v3/versions/loader";
    private const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string NeoForgeReleases = "https://maven.neoforged.net/releases/net/neoforged";
    private const string NeoForgeVersionApi = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged";
    private const string AssetObjectsRoot = "https://resources.download.minecraft.net";

    private static readonly Regex VersionElementRegex = new(@"<version>\s*([^<\s]+)\s*</version>", RegexOptions.Compiled);

    public static async Task<string> InstallVanillaAsync(string mcVersion, IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mcVersion))
            throw new ArgumentException("版本号不能为空", nameof(mcVersion));

        AppPaths.EnsureBaseDirs();
        mcVersion = mcVersion.Trim();

        progress?.Report(InstallProgress.Status("解析版本信息", $"正在获取 Minecraft {mcVersion} 的版本索引…", 0));
        var manifest = await VersionService.GetManifestAsync(false, ct).ConfigureAwait(false);
        var entry = manifest.Versions.FirstOrDefault(x =>
                        string.Equals(x.Id, mcVersion, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"官方版本列表中不存在 Minecraft {mcVersion}");

        var meta = await VersionService.FetchMetaAsync(entry.Url, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(meta.Id))
            meta.Id = entry.Id;
        var id = meta.Id;
        progress?.Report(InstallProgress.Status("解析版本信息", $"{id}（{entry.Type}）", 3));

        var instanceDir = AppPaths.GetInstanceDir(id);
        Directory.CreateDirectory(instanceDir);
        var raw = meta.RawJson ?? throw new InvalidDataException($"版本 {id} 的 JSON 内容为空");
        await File.WriteAllTextAsync(VersionService.GetJsonPath(id), raw, new UTF8Encoding(false), ct)
            .ConfigureAwait(false);

        await VersionService.DownloadClientAsync(meta, progress, ct).ConfigureAwait(false);

        progress?.Report(InstallProgress.Status("下载依赖库", "正在下载依赖库…", 35));
        await VersionService.DownloadLibrariesAsync(meta, progress, ct).ConfigureAwait(false);

        progress?.Report(InstallProgress.Status("解压运行库", "正在解压本地运行库…", 60));
        await ExtractNativesAsync(meta, progress, ct).ConfigureAwait(false);

        await InstallAssetsAsync(meta, progress, ct).ConfigureAwait(false);
        await InstallLoggingAsync(meta, ct).ConfigureAwait(false);

        LogService.Info($"原版 {id} 安装完成");
        progress?.Report(InstallProgress.Status("安装完成", $"Minecraft {id} 安装完成", 100));
        return id;
    }

    public static async Task<List<string>> GetLoaderVersionsAsync(LoaderKind kind, string mcVersion,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mcVersion))
            return new List<string>();

        mcVersion = mcVersion.Trim();
        return kind switch
        {
            LoaderKind.Fabric => await GetFabricVersionsAsync(mcVersion, ct).ConfigureAwait(false),
            LoaderKind.Quilt => await GetQuiltVersionsAsync(mcVersion, ct).ConfigureAwait(false),
            LoaderKind.Forge => await GetForgeVersionsAsync(mcVersion, ct).ConfigureAwait(false),
            LoaderKind.NeoForge => await GetNeoForgeVersionsAsync(mcVersion, ct).ConfigureAwait(false),
            _ => new List<string>()
        };
    }

    public static async Task<string> InstallLoaderAsync(LoaderKind kind, string mcVersion, string? loaderVersion,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mcVersion))
            throw new ArgumentException("版本号不能为空", nameof(mcVersion));

        mcVersion = mcVersion.Trim();
        AppPaths.EnsureBaseDirs();

        if (kind == LoaderKind.Vanilla)
            return await InstallVanillaAsync(mcVersion, progress, ct).ConfigureAwait(false);

        var loader = string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim();
        if (loader is null)
        {
            progress?.Report(InstallProgress.Status("解析版本信息", $"正在获取 {kind} 的版本列表…", 0));
            var available = await GetLoaderVersionsAsync(kind, mcVersion, ct).ConfigureAwait(false);
            if (available.Count == 0)
                throw new InvalidOperationException($"没有找到适用于 Minecraft {mcVersion} 的 {kind} 加载器版本");
            loader = available[0];
        }

        await EnsureVanillaBaseAsync(mcVersion, progress, ct).ConfigureAwait(false);

        return kind switch
        {
            LoaderKind.Fabric or LoaderKind.Quilt =>
                await InstallProfileAsync(kind, mcVersion, loader, progress, ct).ConfigureAwait(false),
            LoaderKind.Forge or LoaderKind.NeoForge =>
                await InstallWithInstallerAsync(kind, mcVersion, loader, progress, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"不支持加载器类型：{kind}")
        };
    }

    /// <summary>把版本 JSON 中的本地运行库解压到实例目录下的 natives 文件夹。</summary>
    public static Task ExtractNativesAsync(VersionMeta meta, IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return Task.Run(() =>
        {
            var effective = string.IsNullOrEmpty(meta.InheritsFrom) ? meta : VersionService.Resolve(meta);
            ExtractNativesTo(effective, Path.Combine(AppPaths.GetInstanceDir(meta.Id), "natives"), progress, ct);
        }, ct);
    }

    private static void ExtractNativesTo(VersionMeta meta, string nativesDir, IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var archives = CollectNativeArchives(meta).ToList();
        if (archives.Count == 0)
            return;

        Directory.CreateDirectory(nativesDir);
        for (var i = 0; i < archives.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (path, exclude) = archives[i];
            var name = Path.GetFileName(path);

            if (!File.Exists(path))
            {
                LogService.Warn($"缺少运行库文件，已跳过解压：{path}");
                continue;
            }

            progress?.Report(InstallProgress.Status("解压运行库", $"正在解压 {name}",
                (i + 1) * 100.0 / archives.Count));

            try
            {
                ExtractArchive(path, nativesDir, exclude, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException($"解压运行库 {name} 失败：{ex.Message}", ex);
            }
        }
    }

    private static IEnumerable<(string Path, List<string>? Exclude)> CollectNativeArchives(VersionMeta meta)
    {
        foreach (var library in VersionService.GetEffectiveLibraries(meta))
        {
            if (library.Natives is { Count: > 0 })
            {
                if (!library.Natives.TryGetValue("windows", out var rawClassifier))
                    continue;
                var classifier = rawClassifier.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");
                var artifact = library.Downloads?.Classifiers?.GetValueOrDefault(classifier);
                var relative = artifact?.Path;
                if (string.IsNullOrEmpty(relative))
                {
                    var basePath = VersionService.GetLibraryRelativePath(library);
                    relative = (basePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                        ? basePath[..^4]
                        : basePath) + "-" + classifier + ".jar";
                }

                yield return (ToLibraryPath(relative), library.Extract?.Exclude);
                continue;
            }

            if (!IsNativeLibrary(library))
                continue;

            var relativePath = library.Downloads?.Artifact?.Path;
            if (string.IsNullOrEmpty(relativePath))
                relativePath = VersionService.GetLibraryRelativePath(library);
            yield return (ToLibraryPath(relativePath), library.Extract?.Exclude);
        }
    }

    private static void ExtractArchive(string archivePath, string targetDir, List<string>? exclude, CancellationToken ct)
    {
        var root = Path.GetFullPath(targetDir);
        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.Contains("..", StringComparison.Ordinal) || normalized.Contains(':'))
                continue;
            if (IsExcluded(normalized, exclude))
                continue;

            var destination = Path.GetFullPath(Path.Combine(root,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(destination))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
    }

    private static bool IsExcluded(string name, List<string>? exclude)
    {
        if (name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            return true;
        if (exclude is null)
            return false;

        foreach (var pattern in exclude)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;
            var trimmed = pattern.Trim().Trim('*');
            if (trimmed.Length == 0)
                continue;
            if (name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsNativeLibrary(LibraryInfo library)
    {
        if (library.Name.Contains(":natives-", StringComparison.OrdinalIgnoreCase))
            return true;

        var parts = library.Name.Split(':');
        if (parts.Length > 3 && parts[3].StartsWith("natives", StringComparison.OrdinalIgnoreCase))
            return true;

        return library.Natives is { Count: > 0 } && library.Downloads?.Artifact?.Url is null;
    }

    private static string ToLibraryPath(string relative)
        => Path.Combine(AppPaths.LibrariesDir, relative.Replace('/', Path.DirectorySeparatorChar));

    private static async Task InstallAssetsAsync(VersionMeta meta, IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var index = meta.AssetIndex;
        if (index is null || string.IsNullOrEmpty(index.Id) || string.IsNullOrEmpty(index.Url))
        {
            LogService.Warn($"{meta.Id} 没有资源索引，已跳过资源文件下载");
            return;
        }

        progress?.Report(InstallProgress.Status("下载资源文件", $"正在下载资源索引 {index.Id}…", 65));
        var indexPath = Path.Combine(AppPaths.AssetsDir, "indexes", index.Id + ".json");
        await EnsureDownloadAsync(index.Url, indexPath, index.Sha1, index.Size, ct).ConfigureAwait(false);

        JsonObject root;
        try
        {
            var text = await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false);
            root = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidDataException("资源索引不是合法的 JSON 对象");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException($"读取资源索引 {index.Id} 失败：{ex.Message}", ex);
        }

        if (root["objects"] is not JsonObject objects)
            throw new InvalidDataException($"资源索引 {index.Id} 缺少 objects 字段");

        var items = new List<DownloadItem>(objects.Count);
        var names = new List<(string Name, string Hash)>(objects.Count);
        foreach (var (name, node) in objects)
        {
            var hash = GetString(node?["hash"]);
            if (string.IsNullOrEmpty(hash) || hash.Length < 2)
                continue;
            var prefix = hash[..2];
            items.Add(new DownloadItem($"{AssetObjectsRoot}/{prefix}/{hash}", $"assets/objects/{prefix}/{hash}",
                hash, GetLong(node?["size"])));
            names.Add((name, hash));
        }

        if (items.Count > 0)
        {
            progress?.Report(InstallProgress.Status("下载资源文件", $"共 {items.Count} 个资源文件", 68));
            var reporter = new Progress<DownloadReport>(report => progress?.Report(new InstallProgress
            {
                Stage = "下载资源文件",
                Detail = $"资源文件 {report.CompletedFiles}/{report.TotalFiles}",
                Percent = report.Percent,
                DoneBytes = report.CompletedBytes,
                TotalBytes = report.TotalBytes
            }));
            await DownloadService.DownloadAllAsync(items, reporter, ct).ConfigureAwait(false);
        }

        var mapToResources = GetBool(root["map_to_resources"]);
        var isLegacy = string.Equals(meta.Assets, "pre-1.6", StringComparison.OrdinalIgnoreCase)
                       || index.Id.StartsWith("legacy", StringComparison.OrdinalIgnoreCase)
                       || mapToResources;
        if (!isLegacy)
            return;

        progress?.Report(InstallProgress.Status("下载资源文件", "正在生成旧版资源目录…", 96));
        var virtualRoot = Path.Combine(AppPaths.AssetsDir, "virtual", "legacy");
        var resourcesRoot = Path.Combine(AppPaths.AssetsDir, "minecraft");
        foreach (var (name, hash) in names)
        {
            ct.ThrowIfCancellationRequested();
            var source = Path.Combine(AppPaths.AssetsDir, "objects", hash[..2], hash);
            if (!File.Exists(source))
                continue;
            CopyIfMissing(source, Path.Combine(virtualRoot, name.Replace('/', Path.DirectorySeparatorChar)));
            if (mapToResources)
                CopyIfMissing(source, Path.Combine(resourcesRoot, name.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    private static async Task InstallLoggingAsync(VersionMeta meta, CancellationToken ct)
    {
        var file = meta.Logging?.Client?.File;
        if (file is null || string.IsNullOrEmpty(file.Id) || string.IsNullOrEmpty(file.Url))
            return;

        var target = Path.Combine(AppPaths.AssetsDir, "log_configs", file.Id);
        await EnsureDownloadAsync(file.Url, target, file.Sha1, file.Size, ct).ConfigureAwait(false);
    }

    private static async Task EnsureDownloadAsync(string url, string path, string? sha1, long size,
        CancellationToken ct)
    {
        if (File.Exists(path))
        {
            var length = new FileInfo(path).Length;
            if (size <= 0 || length == size)
                return;
        }

        await HttpService.DownloadFileAsync(url, path, sha1, null, ct).ConfigureAwait(false);
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (File.Exists(destination))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, false);
    }

    private static async Task<string> InstallProfileAsync(LoaderKind kind, string mcVersion, string loaderVersion,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var api = kind == LoaderKind.Fabric ? FabricLoaderApi : QuiltLoaderApi;
        var url = $"{api}/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";

        progress?.Report(InstallProgress.Status("解析版本信息", $"正在获取 {kind} {loaderVersion} 的版本配置…", 5));
        var json = await HttpService.GetStringAsync(url, ct).ConfigureAwait(false);
        var meta = VersionService.ParseMeta(json);
        if (string.IsNullOrWhiteSpace(meta.Id))
            throw new InvalidDataException($"{kind} 返回的版本配置缺少 id 字段");

        var id = meta.Id;
        Directory.CreateDirectory(AppPaths.GetInstanceDir(id));
        await File.WriteAllTextAsync(VersionService.GetJsonPath(id), json, new UTF8Encoding(false), ct)
            .ConfigureAwait(false);

        progress?.Report(InstallProgress.Status("下载依赖库", $"正在下载 {kind} 依赖库…", 25));
        await VersionService.DownloadLibrariesAsync(meta, progress, ct).ConfigureAwait(false);

        LogService.Info($"已安装 {kind} {id}");
        progress?.Report(InstallProgress.Status("安装完成", $"{id} 安装完成", 100));
        return id;
    }

    private static async Task<string> InstallWithInstallerAsync(LoaderKind kind, string mcVersion,
        string loaderVersion, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var java = JavaService.FindBest()
                   ?? throw new InvalidOperationException("未找到可用的 Java，无法安装 Forge/NeoForge");

        EnsureLauncherProfiles();

        var (url, fileName) = GetInstallerUrl(kind, mcVersion, loaderVersion);
        var installerPath = Path.Combine(AppPaths.CacheDir, fileName);
        Directory.CreateDirectory(AppPaths.CacheDir);

        if (!File.Exists(installerPath) || new FileInfo(installerPath).Length == 0)
        {
            progress?.Report(InstallProgress.Status("下载安装器", fileName, 5));
            var fileProgress = new Progress<(long Done, long Total)>(p => progress?.Report(new InstallProgress
            {
                Stage = "下载安装器",
                Detail = $"{fileName}  {p.Done / 1048576.0:0.0} / {p.Total / 1048576.0:0.0} MB",
                Percent = p.Total > 0 ? p.Done * 100.0 / p.Total : null
            }));
            await HttpService.DownloadFileAsync(url, installerPath, null, fileProgress, ct).ConfigureAwait(false);
        }

        progress?.Report(InstallProgress.Status("安装加载器", $"正在运行 {kind} 安装器…", 20));
        // 官方安装器默认写入当前工作目录，因此把工作目录设为游戏目录，避免依赖参数解析差异。
        var arguments = $"-jar \"{installerPath}\" --installClient \"{AppPaths.GameDir}\"";
        LogService.Info($"运行 {kind} 安装器：{java} {arguments}（工作目录：{AppPaths.GameDir}）");

        var (exitCode, output) = await JavaService.RunAsync(java, arguments, AppPaths.GameDir, line =>
        {
            LogService.Info($"[{kind}] {line}");
            progress?.Report(InstallProgress.Status("安装加载器", line));
        }, ct).ConfigureAwait(false);

        if (exitCode != 0)
        {
            var tail = output.Length > 2000 ? output[^2000..] : output;
            throw new InvalidOperationException($"{kind} 安装器执行失败（退出代码 {exitCode}）：\n{tail}");
        }

        progress?.Report(InstallProgress.Status("安装加载器", "正在查找安装后的版本…", 85));
        var id = FindInstalledLoaderVersion(kind, mcVersion)
                 ?? throw new InvalidOperationException(
                     $"{kind} 安装器已结束，但没有找到继承自 {mcVersion} 的版本，请检查安装器日志");
        var installed = VersionService.LoadLocalMeta(id)
                        ?? throw new InvalidOperationException($"无法读取版本 {id} 的版本配置");
        await VersionService.DownloadLibrariesAsync(VersionService.Resolve(installed), progress, ct)
            .ConfigureAwait(false);

        LogService.Info($"已安装 {kind} {id}");
        progress?.Report(InstallProgress.Status("安装完成", $"{id} 安装完成", 100));
        return id;
    }

    private static (string Url, string FileName) GetInstallerUrl(LoaderKind kind, string mcVersion,
        string loaderVersion)
    {
        if (kind == LoaderKind.Forge)
        {
            var full = $"{mcVersion}-{loaderVersion}";
            return ($"{ForgeMaven}/{full}/forge-{full}-installer.jar", $"forge-{full}-installer.jar");
        }

        if (CompareMcVersion(mcVersion, 1, 20, 1) == 0)
        {
            var full = $"{mcVersion}-{loaderVersion}";
            return ($"{NeoForgeReleases}/forge/{full}/forge-{full}-installer.jar", $"neoforge-{full}-installer.jar");
        }

        return ($"{NeoForgeReleases}/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar",
            $"neoforge-{loaderVersion}-installer.jar");
    }

    private static string? FindInstalledLoaderVersion(LoaderKind kind, string mcVersion)
    {
        if (!Directory.Exists(AppPaths.VersionsDir))
            return null;

        var prefix = kind == LoaderKind.NeoForge ? "neoforge-" : "forge-";
        var keyword = kind == LoaderKind.NeoForge ? "neoforge" : "forge";

        var candidates = new List<(string Name, DateTime Time)>();
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.VersionsDir))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;

            var meta = VersionService.LoadLocalMeta(name);
            if (meta is null || !string.Equals(meta.InheritsFrom, mcVersion, StringComparison.OrdinalIgnoreCase))
                continue;

            candidates.Add((name, Directory.GetLastWriteTimeUtc(dir)));
        }

        return candidates.Count == 0
            ? null
            : candidates.OrderByDescending(x => x.Time).First().Name;
    }

    private static async Task EnsureVanillaBaseAsync(string mcVersion, IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var jsonPath = VersionService.GetJsonPath(mcVersion);
        var jarPath = Path.Combine(AppPaths.GetInstanceDir(mcVersion), mcVersion + ".jar");

        if (!File.Exists(jsonPath))
        {
            progress?.Report(InstallProgress.Status("准备原版文件",
                $"安装加载器前需要先安装 Minecraft {mcVersion}", 0));
            await InstallVanillaAsync(mcVersion, progress, ct).ConfigureAwait(false);
            return;
        }

        if (File.Exists(jarPath))
            return;

        var meta = VersionService.LoadLocalMeta(mcVersion);
        if (meta is null)
            return;

        progress?.Report(InstallProgress.Status("准备原版文件", $"正在下载 Minecraft {mcVersion} 本体…", 0));
        await VersionService.DownloadClientAsync(meta, progress, ct).ConfigureAwait(false);
    }

    private static void EnsureLauncherProfiles()
    {
        Directory.CreateDirectory(AppPaths.GameDir);
        var path = Path.Combine(AppPaths.GameDir, "launcher_profiles.json");
        if (!File.Exists(path))
            File.WriteAllText(path, "{\"profiles\":{}}", new UTF8Encoding(false));
    }

    private static async Task<List<string>> GetFabricVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var json = await HttpService
            .GetStringAsync($"{FabricLoaderApi}/{Uri.EscapeDataString(mcVersion)}", ct)
            .ConfigureAwait(false);

        var result = new List<string>();
        if (JsonNode.Parse(json) is not JsonArray array)
            return result;

        foreach (var item in array)
        {
            var version = GetString(item?["loader"]?["version"]);
            if (!string.IsNullOrWhiteSpace(version) && !result.Contains(version))
                result.Add(version);
        }

        return result;
    }

    private static async Task<List<string>> GetQuiltVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var json = await HttpService
            .GetStringAsync($"{QuiltLoaderApi}/{Uri.EscapeDataString(mcVersion)}", ct)
            .ConfigureAwait(false);

        var result = new List<string>();
        if (JsonNode.Parse(json) is not JsonArray array)
            return result;

        foreach (var item in array)
        {
            var version = GetString(item?["loader"]?["version"]) ?? GetString(item?["version"]);
            if (!string.IsNullOrWhiteSpace(version) && !result.Contains(version))
                result.Add(version);
        }

        return result;
    }

    private static async Task<List<string>> GetForgeVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var xml = await HttpService
            .GetStringAsync($"{ForgeMaven}/maven-metadata.xml", ct)
            .ConfigureAwait(false);

        var prefix = mcVersion + "-";
        var result = new List<string>();
        foreach (Match match in VersionElementRegex.Matches(xml))
        {
            var value = match.Groups[1].Value;
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var loader = value[prefix.Length..];
            if (loader.Length > 0 && !result.Contains(loader))
                result.Add(loader);
        }

        result.Sort((a, b) => CompareVersionStrings(b, a));
        return result;
    }

    private static async Task<List<string>> GetNeoForgeVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var result = new List<string>();
        try
        {
            var parts = ParseMcComponents(mcVersion);
            if (parts is null)
                return result;

            if (CompareMcVersion(mcVersion, 1, 20, 1) == 0)
            {
                var json = await HttpService
                    .GetStringAsync($"{NeoForgeVersionApi}/forge", ct)
                    .ConfigureAwait(false);
                var prefix = mcVersion + "-";
                foreach (var value in ParseNeoForgeVersions(json))
                {
                    if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var loader = value[prefix.Length..];
                    if (loader.Length > 0 && !result.Contains(loader))
                        result.Add(loader);
                }
            }
            else if (CompareMcVersion(mcVersion, 1, 20, 2) >= 0)
            {
                var json = await HttpService
                    .GetStringAsync($"{NeoForgeVersionApi}/neoforge", ct)
                    .ConfigureAwait(false);
                var prefix = $"{parts[1]}.{(parts.Length > 2 ? parts[2] : 0)}.";
                foreach (var value in ParseNeoForgeVersions(json))
                {
                    if (value.StartsWith(prefix, StringComparison.Ordinal) && !result.Contains(value))
                        result.Add(value);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn($"获取 NeoForge 版本列表失败：{ex.Message}");
            result.Clear();
        }

        result.Sort((a, b) => CompareVersionStrings(b, a));
        return result;
    }

    private static List<string> ParseNeoForgeVersions(string json)
    {
        var result = new List<string>();
        if (JsonNode.Parse(json) is not JsonObject root || root["versions"] is not JsonArray array)
            return result;

        foreach (var node in array)
        {
            var value = GetString(node);
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }

        return result;
    }

    private static int[]? ParseMcComponents(string mcVersion)
    {
        var parts = mcVersion.Split('.');
        if (parts.Length < 2)
            return null;

        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var value))
                return null;
            result[i] = value;
        }

        if (result[0] != 1)
            return null;
        return result;
    }

    private static int CompareMcVersion(string mcVersion, int major, int minor, int patch)
    {
        var parts = ParseMcComponents(mcVersion);
        if (parts is null)
            return -1;

        var values = new[] { parts[0], parts[1], parts.Length > 2 ? parts[2] : 0 };
        var target = new[] { major, minor, patch };
        for (var i = 0; i < 3; i++)
        {
            if (values[i] != target[i])
                return values[i].CompareTo(target[i]);
        }

        return 0;
    }

    private static int CompareVersionStrings(string a, string b)
    {
        var left = SplitVersion(a);
        var right = SplitVersion(b);
        var length = Math.Max(left.Count, right.Count);
        for (var i = 0; i < length; i++)
        {
            var x = i < left.Count ? left[i] : 0;
            var y = i < right.Count ? right[i] : 0;
            if (x != y)
                return x.CompareTo(y);
        }

        return 0;
    }

    private static List<int> SplitVersion(string value)
    {
        var result = new List<int>();
        foreach (var part in value.Split('.', '-', '+', '_'))
            result.Add(int.TryParse(part, out var number) ? number : 0);
        return result;
    }

    private static string? GetString(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<string>(out var text))
            return text;
        return value.ToJsonString().Trim('"');
    }

    private static long GetLong(JsonNode? node)
    {
        if (node is not JsonValue value)
            return 0;
        if (value.TryGetValue<long>(out var number))
            return number;
        if (value.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed))
            return parsed;
        return 0;
    }

    private static bool GetBool(JsonNode? node)
    {
        if (node is not JsonValue value)
            return false;
        if (value.TryGetValue<bool>(out var flag))
            return flag;
        if (value.TryGetValue<int>(out var number))
            return number != 0;
        if (value.TryGetValue<string>(out var text))
            return bool.TryParse(text, out var parsed) && parsed;
        return false;
    }
}
