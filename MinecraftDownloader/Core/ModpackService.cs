using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MinecraftDownloader.Core.Models;

namespace MinecraftDownloader.Core;

public static class ModpackService
{
    private const string IndexEntryName = "modrinth.index.json";

    private static readonly string[] OverrideTargets =
    {
        "config", "options.txt", "servers.dat", "resourcepacks", "shaderpacks", "defaultconfigs"
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions IndexWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<string> InstallMrpackAsync(string mrpackPath,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mrpackPath) || !File.Exists(mrpackPath))
            throw new FileNotFoundException("找不到整合包文件。", mrpackPath);

        var index = ReadIndex(mrpackPath);
        var packName = string.IsNullOrWhiteSpace(index.Name)
            ? Path.GetFileNameWithoutExtension(mrpackPath)
            : index.Name!.Trim();
        if (string.IsNullOrWhiteSpace(packName))
            packName = "modpack";

        var mcVersion = GetDependency(index.Dependencies, "minecraft")
                        ?? GetDependency(index.Dependencies, "minecraft_version");
        if (string.IsNullOrWhiteSpace(mcVersion))
            throw new InvalidOperationException("整合包未声明 Minecraft 版本（dependencies.minecraft）。");
        mcVersion = mcVersion!.Trim();

        var (loaderKind, loaderVersion, loaderLabel) = ResolveLoader(index.Dependencies);

        string? ownedInstanceId = null;
        try
        {
            progress?.Report(InstallProgress.Status("安装游戏本体", $"Minecraft {mcVersion}", 0));
            var vanillaId = await Installer.InstallVanillaAsync(mcVersion, progress, ct)
                .ConfigureAwait(false);
            var loaderId = vanillaId;

            if (loaderKind is not null)
            {
                progress?.Report(InstallProgress.Status($"安装 {loaderLabel}",
                    loaderVersion ?? mcVersion, null));
                var installed = await Installer
                    .InstallLoaderAsync(loaderKind.Value, mcVersion, loaderVersion, progress, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(installed))
                    loaderId = installed;
            }

            var targetId = AllocateInstanceId(SanitizeInstanceId(packName));
            string instanceId;
            try
            {
                // 复制而不是移动：保留原有的加载器版本，避免重复安装同一加载器时破坏已有实例。
                CloneInstance(loaderId, targetId);
                RewriteInstanceIdentity(targetId, loaderId);
                instanceId = targetId;
                ownedInstanceId = targetId;
            }
            catch (Exception ex)
            {
                LogService.Warn($"无法创建整合包实例 {targetId}，沿用 {loaderId}：{ex.Message}");
                instanceId = loaderId;
            }

            var instanceDir = AppPaths.GetInstanceDir(instanceId);
            Directory.CreateDirectory(instanceDir);

            await DownloadIndexFilesAsync(index, instanceId, progress, ct).ConfigureAwait(false);

            progress?.Report(InstallProgress.Status("解压覆盖文件", "overrides", 90));
            await ExtractOverridesAsync(mrpackPath, instanceDir, progress, ct).ConfigureAwait(false);

            InstanceService.WriteMetadata(instanceId, packName, mcVersion, loaderLabel);
            progress?.Report(InstallProgress.Status("安装整合包", packName, 100));
            LogService.Info($"整合包《{packName}》安装完成：{instanceId}");
            return instanceId;
        }
        catch
        {
            if (ownedInstanceId is not null)
            {
                try
                {
                    InstanceService.Delete(ownedInstanceId);
                }
                catch (Exception cleanupError)
                {
                    LogService.Warn($"清理未完成的整合包实例 {ownedInstanceId} 失败：{cleanupError.Message}");
                }
            }

            throw;
        }
    }

    public static async Task<string> DownloadAndInstallAsync(string url, string fileName,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("整合包下载地址为空。", nameof(url));

        var name = string.IsNullOrWhiteSpace(fileName) ? "pack.mrpack" : fileName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        if (!name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            name += ".mrpack";

        var directory = Path.Combine(AppPaths.CacheDir, "modpacks");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);

        progress?.Report(InstallProgress.Status("下载整合包", name, 0));
        var fileProgress = new SynchronousProgress<(long Done, long Total)>(p => progress?.Report(
            new InstallProgress
            {
                Stage = "下载整合包",
                Detail = $"{name}  {p.Done / 1048576.0:0.0} / {p.Total / 1048576.0:0.0} MB",
                Percent = p.Total > 0 ? p.Done * 100.0 / p.Total : null,
                DoneBytes = p.Done,
                TotalBytes = p.Total
            }));

        await HttpService.DownloadFileAsync(url, path, null, fileProgress, ct).ConfigureAwait(false);
        return await InstallMrpackAsync(path, progress, ct).ConfigureAwait(false);
    }

    public static async Task ExportMrpackAsync(string instanceId, string outputPath,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("实例 id 为空。", nameof(instanceId));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("输出路径为空。", nameof(outputPath));

        var info = InstanceService.Get(instanceId)
                   ?? throw new InvalidOperationException($"找不到实例：{instanceId}");
        var instanceDir = AppPaths.GetInstanceDir(instanceId);

        VersionMeta? resolved = null;
        var localMeta = VersionService.LoadLocalMeta(instanceId);
        if (localMeta is not null)
        {
            try
            {
                resolved = VersionService.Resolve(localMeta);
            }
            catch (Exception ex)
            {
                LogService.Warn($"解析实例 {instanceId} 的版本信息失败：{ex.Message}");
                resolved = localMeta;
            }
        }

        var mcVersion = !string.IsNullOrWhiteSpace(info.McVersion)
            ? info.McVersion.Trim()
            : resolved?.InheritsFrom ?? instanceId;

        var loaderKey = LoaderDependencyKey(info.Loader);
        var loaderVersion = loaderKey is null
            ? null
            : ParseLoaderVersion(instanceId, mcVersion) ?? FindLoaderVersion(resolved, info.Loader, mcVersion);

        var index = new ExportIndex
        {
            VersionId = instanceId,
            Name = info.DisplayName,
            Summary = "由 MinecraftDownloader 导出的整合包"
        };
        index.Dependencies["minecraft"] = mcVersion;
        if (loaderKey is not null)
        {
            if (!string.IsNullOrWhiteSpace(loaderVersion))
                index.Dependencies[loaderKey] = loaderVersion!;
            else
                LogService.Warn($"未能确定 {info.Loader} 的版本号，导出的整合包将不包含加载器依赖。");
        }

        progress?.Report(InstallProgress.Status("收集模组", "", 0));
        var modsDir = Path.Combine(instanceDir, "mods");
        var jars = Directory.Exists(modsDir)
            ? Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

        var jarHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<string>();
        foreach (var jar in jars)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var sha1 = await HashService.Sha1FileAsync(jar, ct).ConfigureAwait(false);
                jarHashes[sha1] = jar;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Warn($"计算 {Path.GetFileName(jar)} 的 SHA1 失败：{ex.Message}");
                unmatched.Add(jar);
            }
        }

        progress?.Report(InstallProgress.Status("查询 Modrinth", $"{jars.Length} 个模组", 5));
        var matches = await ModrinthService.LookupBySha1Async(jarHashes.Keys, ct).ConfigureAwait(false);

        var zipEntries = new List<(string EntryName, string SourcePath)>();
        foreach (var (sha1, jar) in jarHashes)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(jar);
            var entry = matches.TryGetValue(sha1, out var version) && version is not null
                ? await BuildFileEntryAsync(version, sha1, jar, fileName, ct).ConfigureAwait(false)
                : null;
            if (entry is not null)
                index.Files.Add(entry);
            else
                zipEntries.Add(($"overrides/mods/{fileName}", jar));
        }

        foreach (var jar in unmatched)
            zipEntries.Add(($"overrides/mods/{Path.GetFileName(jar)}", jar));

        foreach (var relative in OverrideTargets)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(instanceDir, relative);
            if (File.Exists(path))
            {
                zipEntries.Add(($"overrides/{relative}", path));
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetRelativePath(instanceDir, file).Replace('\\', '/');
                    if (name.Equals("instance.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    zipEntries.Add(($"overrides/{name}", file));
                }
            }
        }

        var fullOutput = Path.GetFullPath(outputPath);
        var outputDir = Path.GetDirectoryName(fullOutput);
        if (!string.IsNullOrWhiteSpace(outputDir))
            Directory.CreateDirectory(outputDir);

        try
        {
            if (File.Exists(fullOutput))
                File.Delete(fullOutput);

            progress?.Report(InstallProgress.Status("写入整合包",
                $"{index.Files.Count} 个模组来自 Modrinth", 10));
            using (var zip = ZipFile.Open(fullOutput, ZipArchiveMode.Create))
            {
                await WriteTextEntryAsync(zip, IndexEntryName,
                    JsonSerializer.Serialize(index, IndexWriteOptions), ct).ConfigureAwait(false);

                var total = Math.Max(1, zipEntries.Count);
                for (var i = 0; i < zipEntries.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (entryName, sourcePath) = zipEntries[i];
                    await AddFileEntryAsync(zip, entryName, sourcePath, ct).ConfigureAwait(false);
                    progress?.Report(InstallProgress.Status("写入整合包",
                        Path.GetFileName(sourcePath), 10 + (i + 1) * 90.0 / total));
                }
            }
        }
        catch
        {
            try
            {
                if (File.Exists(fullOutput))
                    File.Delete(fullOutput);
            }
            catch
            {
                // 忽略清理失败
            }

            throw;
        }

        progress?.Report(InstallProgress.Status("导出整合包", fullOutput, 100));
        LogService.Info($"已导出整合包：{fullOutput}");
    }

    private static MrpackIndex ReadIndex(string mrpackPath)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        var entry = zip.GetEntry(IndexEntryName)
                    ?? zip.Entries.FirstOrDefault(x =>
                        x.FullName.Equals(IndexEntryName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        "不是有效的 Modrinth 整合包：缺少 modrinth.index.json。");

        using var stream = entry.Open();
        var index = JsonSerializer.Deserialize<MrpackIndex>(stream, JsonDefaults.Options);
        if (index is null)
            throw new InvalidOperationException("无法解析整合包中的 modrinth.index.json。");
        index.Files ??= new List<MrpackFile>();
        return index;
    }

    private static async Task DownloadIndexFilesAsync(MrpackIndex index, string instanceId,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var files = index.Files
            .Where(x => !string.Equals(x.Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0)
            return;

        progress?.Report(InstallProgress.Status("下载整合包文件", $"共 {files.Count} 个文件", 0));
        var items = new List<DownloadItem>(files.Count);
        foreach (var file in files)
        {
            var relative = NormalizeRelativePath(file.Path);
            if (relative is null)
            {
                LogService.Warn($"跳过整合包中的非法路径：{file.Path}");
                continue;
            }

            var url = file.Downloads?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException($"整合包文件 {file.Path} 缺少下载地址。");

            var sha1 = file.Hashes is not null && file.Hashes.TryGetValue("sha1", out var hash) ? hash : null;
            items.Add(new DownloadItem(url!, Path.Combine("versions", instanceId, relative), sha1,
                file.FileSize));
        }

        if (items.Count == 0)
            return;

        var reporter = new SynchronousProgress<DownloadReport>(report => progress?.Report(
            new InstallProgress
            {
                Stage = "下载整合包文件",
                Detail = report.CurrentFile,
                Percent = report.Percent,
                DoneBytes = report.CompletedBytes,
                TotalBytes = report.TotalBytes
            }));

        await DownloadService.DownloadAllAsync(items, reporter, ct).ConfigureAwait(false);
    }

    private static async Task ExtractOverridesAsync(string mrpackPath, string instanceDir,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        var root = Path.GetFullPath(instanceDir);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var entries = zip.Entries
            .Select(entry => (Entry: entry, Prefix: GetOverridePrefix(entry.FullName)))
            .Where(x => x.Prefix is not null)
            .OrderBy(x => x.Prefix!.StartsWith("client-", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();

        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (entry, prefix) = entries[i];
            if (entry.Name.Length == 0)
                continue;

            var relative = NormalizeRelativePath(entry.FullName[prefix!.Length..]);
            if (relative is null)
            {
                LogService.Warn($"跳过整合包中的非法路径：{entry.FullName}");
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(instanceDir, relative));
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Warn($"跳过整合包中的非法路径：{entry.FullName}");
                continue;
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write,
                FileShare.None, 81920, true);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);

            if ((i & 15) == 0)
                progress?.Report(InstallProgress.Status("解压覆盖文件", entry.FullName,
                    90 + i * 10.0 / Math.Max(1, entries.Count)));
        }
    }

    private static string? GetOverridePrefix(string fullName)
    {
        if (fullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
            return "overrides/";
        if (fullName.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase))
            return "client-overrides/";
        return null;
    }

    private static async Task<ExportFileEntry?> BuildFileEntryAsync(ModrinthVersion version, string sha1,
        string jarPath, string fileName, CancellationToken ct)
    {
        var files = version.Files ?? new List<ModrinthFile>();
        var matched = files.FirstOrDefault(f => f.Hashes is not null &&
                                                f.Hashes.TryGetValue("sha1", out var hash) &&
                                                string.Equals(hash, sha1, StringComparison.OrdinalIgnoreCase))
                      ?? files.FirstOrDefault(f => f.Primary)
                      ?? files.FirstOrDefault();
        if (matched is null || string.IsNullOrWhiteSpace(matched.Url))
            return null;

        string sha512;
        if (matched.Hashes is not null && matched.Hashes.TryGetValue("sha512", out var known) &&
            !string.IsNullOrWhiteSpace(known))
        {
            sha512 = known;
        }
        else
        {
            sha512 = await HashService.Sha512FileAsync(jarPath, ct).ConfigureAwait(false);
        }

        var size = matched.Size > 0 ? matched.Size : new FileInfo(jarPath).Length;
        return new ExportFileEntry
        {
            Path = "mods/" + fileName,
            Hashes = new Dictionary<string, string> { ["sha1"] = sha1, ["sha512"] = sha512 },
            Downloads = new List<string> { matched.Url },
            FileSize = size,
            Env = new Dictionary<string, string>
            {
                ["client"] = "required",
                ["server"] = "unsupported"
            }
        };
    }

    private static async Task WriteTextEntryAsync(ZipArchive zip, string entryName, string content,
        CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task AddFileEntryAsync(ZipArchive zip, string entryName, string sourcePath,
        CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, true);
        await using var output = entry.Open();
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    private static (LoaderKind? Kind, string? Version, string Label) ResolveLoader(
        Dictionary<string, string>? dependencies)
    {
        if (GetDependency(dependencies, "fabric-loader") is { } fabric)
            return (LoaderKind.Fabric, fabric, "Fabric");
        if (GetDependency(dependencies, "quilt-loader") is { } quilt)
            return (LoaderKind.Quilt, quilt, "Quilt");
        if (GetDependency(dependencies, "neoforge") is { } neoforge)
            return (LoaderKind.NeoForge, neoforge, "NeoForge");
        if (GetDependency(dependencies, "forge") is { } forge)
            return (LoaderKind.Forge, forge, "Forge");
        return (null, null, "原版");
    }

    private static string? GetDependency(Dictionary<string, string>? dependencies, string key)
    {
        if (dependencies is null)
            return null;
        foreach (var (name, value) in dependencies)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static string? LoaderDependencyKey(string? loader) => loader switch
    {
        "Fabric" => "fabric-loader",
        "Quilt" => "quilt-loader",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        _ => null
    };

    private static string? ParseLoaderVersion(string instanceId, string mcVersion)
    {
        string? remainder = null;
        foreach (var prefix in new[] { "fabric-loader-", "quilt-loader-", "neoforge-", "forge-" })
        {
            if (!instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            remainder = instanceId[prefix.Length..];
            break;
        }

        if (remainder is null)
        {
            var index = instanceId.IndexOf("forge-", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                remainder = instanceId[(index + "forge-".Length)..];
        }

        if (string.IsNullOrWhiteSpace(remainder))
            return null;

        if (!string.IsNullOrWhiteSpace(mcVersion) &&
            remainder.EndsWith("-" + mcVersion, StringComparison.OrdinalIgnoreCase))
            remainder = remainder[..^(mcVersion.Length + 1)];

        remainder = remainder.Trim('-', '.', ' ');
        return string.IsNullOrWhiteSpace(remainder) ? null : remainder;
    }

    private static string? FindLoaderVersion(VersionMeta? meta, string? loader, string mcVersion)
    {
        if (meta is null || meta.Libraries.Count == 0)
            return null;

        var prefixes = loader switch
        {
            "Fabric" => new[] { "net.fabricmc:fabric-loader" },
            "Quilt" => new[] { "org.quiltmc:quilt-loader" },
            "Forge" => new[] { "net.minecraftforge:forge", "net.minecraftforge:fmlloader" },
            "NeoForge" => new[] { "net.neoforged:neoforge", "net.neoforged.fancymodloader:loader" },
            _ => Array.Empty<string>()
        };

        foreach (var library in meta.Libraries)
        {
            foreach (var prefix in prefixes)
            {
                if (!library.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = library.Name.Split(':');
                if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[2]))
                    continue;
                var value = parts[2].Trim();
                if (!string.IsNullOrWhiteSpace(mcVersion) &&
                    value.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase))
                    value = value[(mcVersion.Length + 1)..];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static string SanitizeInstanceId(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or ' ')
                builder.Append(ch);
        }

        var id = builder.ToString().Trim().TrimEnd('.', ' ');
        if (id.Length > 64)
            id = id[..64].Trim().TrimEnd('.', ' ');
        return id.Length == 0 ? "modpack" : id;
    }

    private static string AllocateInstanceId(string baseId)
    {
        var candidate = baseId;
        var suffix = 2;
        while (Directory.Exists(AppPaths.GetInstanceDir(candidate)) ||
               File.Exists(VersionService.GetJsonPath(candidate)))
        {
            candidate = $"{baseId}-{suffix++}";
        }

        return candidate;
    }

    private static void CloneInstance(string sourceId, string targetId)
    {
        var source = AppPaths.GetInstanceDir(sourceId);
        var target = AppPaths.GetInstanceDir(targetId);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"找不到版本目录：{source}");
        if (Directory.Exists(target))
            throw new IOException($"目标实例已存在：{targetId}");
        CopyDirectory(source, target);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static void RewriteInstanceIdentity(string id, string oldId)
    {
        var dir = AppPaths.GetInstanceDir(id);
        var jsonPath = Path.Combine(dir, id + ".json");
        var oldJsonPath = Path.Combine(dir, oldId + ".json");
        if (!File.Exists(jsonPath) && File.Exists(oldJsonPath))
            File.Move(oldJsonPath, jsonPath);

        var oldJarPath = Path.Combine(dir, oldId + ".jar");
        var jarPath = Path.Combine(dir, id + ".jar");
        if (!string.Equals(oldJarPath, jarPath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(oldJarPath))
            File.Move(oldJarPath, jarPath, true);

        if (!File.Exists(jsonPath))
        {
            LogService.Warn($"实例 {id} 缺少版本 JSON，无法写入 id。");
            return;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(jsonPath)) is JsonObject node)
            {
                node["id"] = id;
                File.WriteAllText(jsonPath, node.ToJsonString(JsonWriteOptions),
                    new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"写入实例 {id} 的版本 JSON 失败：{ex.Message}");
        }
    }

    private static string? NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var cleaned = path.Replace('\\', '/').Trim();
        if (cleaned.StartsWith('/') || cleaned.Contains(':'))
            return null;

        var parts = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        foreach (var part in parts)
        {
            if (part is "." or "..")
                return null;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;
        }

        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    private sealed class MrpackIndex
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; set; } = 1;

        [JsonPropertyName("game")]
        public string? Game { get; set; }

        [JsonPropertyName("versionId")]
        public string? VersionId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("files")]
        public List<MrpackFile> Files { get; set; } = new();

        [JsonPropertyName("dependencies")]
        public Dictionary<string, string>? Dependencies { get; set; }
    }

    private sealed class MrpackFile
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("hashes")]
        public Dictionary<string, string>? Hashes { get; set; }

        [JsonPropertyName("downloads")]
        public List<string>? Downloads { get; set; }

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("env")]
        public MrpackEnv? Env { get; set; }
    }

    private sealed class MrpackEnv
    {
        [JsonPropertyName("client")]
        public string? Client { get; set; }

        [JsonPropertyName("server")]
        public string? Server { get; set; }
    }

    private sealed class ExportIndex
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; set; } = 1;

        [JsonPropertyName("game")]
        public string Game { get; set; } = "minecraft";

        [JsonPropertyName("versionId")]
        public string VersionId { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("files")]
        public List<ExportFileEntry> Files { get; set; } = new();

        [JsonPropertyName("dependencies")]
        public Dictionary<string, string> Dependencies { get; set; } = new();
    }

    private sealed class ExportFileEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("hashes")]
        public Dictionary<string, string> Hashes { get; set; } = new();

        [JsonPropertyName("downloads")]
        public List<string> Downloads { get; set; } = new();

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("env")]
        public Dictionary<string, string>? Env { get; set; }
    }
}
