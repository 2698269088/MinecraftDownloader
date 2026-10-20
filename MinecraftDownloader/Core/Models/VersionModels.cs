using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MinecraftDownloader.Core.Models;

public sealed class VersionManifest
{
    public LatestVersion? Latest { get; set; }
    public List<VersionSummary> Versions { get; set; } = new();
}

public sealed class LatestVersion
{
    public string Release { get; set; } = "";
    public string Snapshot { get; set; } = "";
}

public sealed class VersionSummary
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime? ReleaseTime { get; set; }

    public override string ToString() => Id;
}

public sealed class VersionMeta
{
    public string Id { get; set; } = "";
    public string? Type { get; set; }
    public string? MainClass { get; set; }
    public string? Assets { get; set; }
    public string? InheritsFrom { get; set; }
    public AssetIndexInfo? AssetIndex { get; set; }
    public VersionDownloads? Downloads { get; set; }
    public List<LibraryInfo> Libraries { get; set; } = new();
    public JavaVersionInfo? JavaVersion { get; set; }
    public List<GameArgument> GameArguments { get; set; } = new();
    public List<GameArgument> JvmArguments { get; set; } = new();
    public string? MinecraftArguments { get; set; }
    public LoggingInfo? Logging { get; set; }
    public string? RawJson { get; set; }

    [JsonIgnore]
    public string? ClientUrl => Downloads?.Client?.Url;
}

public sealed class AssetIndexInfo
{
    public string Id { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public long TotalSize { get; set; }
    public string? Url { get; set; }
}

public sealed class VersionDownloads
{
    public DownloadArtifact? Client { get; set; }
    public DownloadArtifact? Server { get; set; }
}

public sealed class DownloadArtifact
{
    public string? Path { get; set; }
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
}

public sealed class LibraryInfo
{
    public string Name { get; set; } = "";
    public LibraryDownloads? Downloads { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public List<Rule>? Rules { get; set; }
    public ExtractInfo? Extract { get; set; }
    public string? Url { get; set; }
}

public sealed class LibraryDownloads
{
    public DownloadArtifact? Artifact { get; set; }
    public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

public sealed class ExtractInfo
{
    public List<string>? Exclude { get; set; }
}

public sealed class Rule
{
    public string Action { get; set; } = "allow";
    public OsInfo? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsInfo
{
    public string? Name { get; set; }
    public string? Arch { get; set; }
    public string? Version { get; set; }
}

public sealed class JavaVersionInfo
{
    public string? Component { get; set; }
    public int MajorVersion { get; set; }
}

public sealed class LoggingInfo
{
    public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    public string? Argument { get; set; }
    public LoggingFile? File { get; set; }
}

public sealed class LoggingFile
{
    public string Id { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Url { get; set; }
}

/// <summary>版本 JSON 中的参数既可能是字符串，也可能是带规则的对象。</summary>
public sealed class GameArgument
{
    public List<Rule>? Rules { get; set; }
    public List<string> Values { get; set; } = new();

    public static List<GameArgument> Parse(JsonNode? node)
    {
        var result = new List<GameArgument>();
        if (node is not JsonArray array)
            return result;

        foreach (var element in array)
        {
            if (element is JsonValue value && value.TryGetValue<string>(out var text))
            {
                result.Add(new GameArgument { Values = { text } });
                continue;
            }

            if (element is not JsonObject obj)
                continue;

            var argument = new GameArgument();
            if (obj["rules"] is JsonArray rulesArray)
            {
                argument.Rules = System.Text.Json.JsonSerializer.Deserialize<List<Rule>>(rulesArray,
                    JsonDefaults.Options);
            }

            var valueNode = obj["value"];
            if (valueNode is JsonValue single && single.TryGetValue<string>(out var singleText))
            {
                argument.Values.Add(singleText);
            }
            else if (valueNode is JsonArray valuesArray)
            {
                foreach (var item in valuesArray)
                {
                    if (item is JsonValue v && v.TryGetValue<string>(out var s))
                        argument.Values.Add(s);
                }
            }

            result.Add(argument);
        }

        return result;
    }
}
