using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftDownloader.Core;

public static class AppPaths
{
    public static string LauncherDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MinecraftDownloader");

    public static string ConfigFile => Path.Combine(LauncherDir, "config.json");

    public static string GameDir => LauncherConfig.Current.GameDir;

    public static string VersionsDir => Path.Combine(GameDir, "versions");

    public static string LibrariesDir => Path.Combine(GameDir, "libraries");

    public static string AssetsDir => Path.Combine(GameDir, "assets");

    public static string LogsDir => Path.Combine(GameDir, "logs");

    public static string CacheDir => Path.Combine(LauncherDir, "cache");

    public static string GetInstanceDir(string id) => Path.Combine(VersionsDir, id);

    public static string GetModsDir(string id) => Path.Combine(GetInstanceDir(id), "mods");

    public static void EnsureBaseDirs()
    {
        Directory.CreateDirectory(LauncherDir);
        Directory.CreateDirectory(GameDir);
        Directory.CreateDirectory(VersionsDir);
        Directory.CreateDirectory(LibrariesDir);
        Directory.CreateDirectory(AssetsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CacheDir);
    }
}

public sealed class LauncherConfig
{
    public string GameDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    public string JavaPath { get; set; } = "";
    public int DownloadThreads { get; set; } = 24;
    public bool UseMirror { get; set; } = true;
    public string LastInstance { get; set; } = "";

    [JsonIgnore]
    public static LauncherConfig Current { get; private set; } = new();

    public static event Action? Changed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LauncherDir);
            if (File.Exists(AppPaths.ConfigFile))
            {
                var text = File.ReadAllText(AppPaths.ConfigFile);
                Current = JsonSerializer.Deserialize<LauncherConfig>(text, JsonOptions) ?? new LauncherConfig();
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取配置失败，已使用默认配置：{ex.Message}");
            Current = new LauncherConfig();
        }

        if (string.IsNullOrWhiteSpace(Current.GameDir))
            Current.GameDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        if (Current.DownloadThreads is < 1 or > 64)
            Current.DownloadThreads = 24;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LauncherDir);
            File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            LogService.Warn($"保存配置失败：{ex.Message}");
        }

        Changed?.Invoke();
    }
}

public static class LogService
{
    private static readonly object Locker = new();
    private static readonly List<string> Lines = new();

    public static event Action<string>? Logged;

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (Locker)
        {
            Lines.Add(line);
            if (Lines.Count > 2000)
                Lines.RemoveRange(0, 500);
        }

        Logged?.Invoke(line);
    }

    public static void Info(string message) => Write(message);
    public static void Warn(string message) => Write("警告：" + message);
    public static void Error(string message) => Write("错误：" + message);

    public static string[] Snapshot()
    {
        lock (Locker)
            return Lines.ToArray();
    }
}
