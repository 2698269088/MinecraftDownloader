using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinecraftDownloader.Core.Models;

namespace MinecraftDownloader.Core;

public sealed class InstanceInfo
{
    public string Id { get; set; } = "";
    public string Directory { get; set; } = "";
    public string JsonPath { get; set; } = "";
    public string Loader { get; set; } = "原版";
    public string McVersion { get; set; } = "";
    public string Type { get; set; } = "release";
    public string Description { get; set; } = "";
    public DateTime LastWrite { get; set; }
    public bool IsLoader => Loader is not "原版";

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Id : Description;
    public string SubTitle => IsLoader ? $"{Loader} · {McVersion}" : $"原版 · {McVersion}";

    public override string ToString() => DisplayName;
}

public static class InstanceService
{
    public static List<InstanceInfo> GetAll()
    {
        var result = new List<InstanceInfo>();
        if (!Directory.Exists(AppPaths.VersionsDir))
            return result;

        foreach (var dir in Directory.GetDirectories(AppPaths.VersionsDir))
        {
            var id = Path.GetFileName(dir);
            var jsonPath = Path.Combine(dir, id + ".json");
            if (!File.Exists(jsonPath))
                continue;

            var info = new InstanceInfo
            {
                Id = id,
                Directory = dir,
                JsonPath = jsonPath,
                LastWrite = File.GetLastWriteTime(dir)
            };

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject;
                if (node is null)
                    continue;
                info.McVersion = node["inheritsFrom"]?.GetValue<string>() ?? id;
                info.Type = node["type"]?.GetValue<string>() ?? "release";
                var (loader, description) = DetectLoader(id, node);
                info.Loader = loader;
                if (description is not null)
                    info.Description = description;
            }
            catch (Exception ex)
            {
                LogService.Warn($"解析版本 {id} 失败：{ex.Message}");
                info.McVersion = id;
            }

            var metadataPath = Path.Combine(dir, "instance.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<InstanceMetadata>(
                        File.ReadAllText(metadataPath), JsonDefaults.Options);
                    if (meta is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(meta.Name))
                            info.Description = meta.Name;
                        if (!string.IsNullOrWhiteSpace(meta.McVersion))
                            info.McVersion = meta.McVersion;
                        if (!string.IsNullOrWhiteSpace(meta.Loader))
                            info.Loader = meta.Loader;
                    }
                }
                catch
                {
                    // 忽略损坏的元数据
                }
            }

            result.Add(info);
        }

        return result.OrderByDescending(x => x.LastWrite).ToList();
    }

    private static (string Loader, string? Description) DetectLoader(string id, JsonObject node)
    {
        if (id.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
            return ("Fabric", null);
        if (id.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase))
            return ("Quilt", null);
        if (id.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
            return ("NeoForge", null);
        if (id.StartsWith("forge-", StringComparison.OrdinalIgnoreCase))
            return ("Forge", null);
        if (id.Contains("optifine", StringComparison.OrdinalIgnoreCase))
            return ("OptiFine", null);
        var type = node["type"]?.GetValue<string>();
        return ("原版", null);
    }

    public static InstanceInfo? Get(string id) => GetAll().FirstOrDefault(x => x.Id == id);

    public static string GetModsDir(string id)
    {
        var dir = Path.Combine(AppPaths.GetInstanceDir(id), "mods");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void WriteMetadata(string id, string name, string mcVersion, string loader)
    {
        var dir = AppPaths.GetInstanceDir(id);
        Directory.CreateDirectory(dir);
        var metadata = new InstanceMetadata { Name = name, McVersion = mcVersion, Loader = loader };
        File.WriteAllText(Path.Combine(dir, "instance.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Delete(string id)
    {
        var dir = AppPaths.GetInstanceDir(id);
        if (!Directory.Exists(dir))
            return;
        Directory.Delete(dir, true);
    }

    public static string GetGameVersionJson(string id) => VersionService.GetJsonPath(id);
}

public sealed class InstanceMetadata
{
    public string Name { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "";
    public string? ModpackVersion { get; set; }
    public string? ModpackSource { get; set; }
}
