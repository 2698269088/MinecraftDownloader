using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace MinecraftDownloader.Core;

public sealed record JavaInfo(string Path, int Major, string Version)
{
    public string Display => $"Java {Major}  ({Path})";

    public override string ToString() => Display;
}

public static class JavaService
{
    private static List<JavaInfo>? _cache;

    public static List<JavaInfo> Scan(bool force = false)
    {
        if (_cache is not null && !force)
            return _cache;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfExists(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                candidates.Add(path);
        }

        AddIfExists(LauncherConfig.Current.JavaPath);

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            AddIfExists(Path.Combine(javaHome, "bin", "javaw.exe"));
            AddIfExists(Path.Combine(javaHome, "bin", "java.exe"));
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            AddIfExists(Path.Combine(dir.Trim('"'), "javaw.exe"));
            AddIfExists(Path.Combine(dir.Trim('"'), "java.exe"));
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"),
            Path.Combine(AppPaths.GameDir, "runtime")
        };

        var vendors = new[]
        {
            "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu", "BellSoft",
            "Amazon Corretto", "GraalVM", "Semeru", "Temurin"
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var vendor in vendors)
            {
                var vendorDir = Path.Combine(root, vendor);
                if (!Directory.Exists(vendorDir))
                    continue;
                foreach (var dir in SafeEnumerate(vendorDir))
                {
                    AddIfExists(Path.Combine(dir, "bin", "javaw.exe"));
                    AddIfExists(Path.Combine(dir, "bin", "java.exe"));
                }
            }

            foreach (var dir in SafeEnumerate(root))
            {
                if (Path.GetFileName(dir).StartsWith("jdk", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(dir).StartsWith("jre", StringComparison.OrdinalIgnoreCase))
                {
                    AddIfExists(Path.Combine(dir, "bin", "javaw.exe"));
                    AddIfExists(Path.Combine(dir, "bin", "java.exe"));
                }
            }
        }

        var result = new List<JavaInfo>();
        foreach (var path in candidates)
        {
            var info = Parse(path);
            if (info is not null && result.All(x => !string.Equals(x.Path, info.Path, StringComparison.OrdinalIgnoreCase)))
                result.Add(info);
        }

        _cache = result.OrderByDescending(x => x.Major).ToList();
        return _cache;
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static JavaInfo? Parse(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null)
                return null;
            var text = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            var match = Regex.Match(text, "version \"(?<v>[^\"]+)\"");
            if (!match.Success)
                return null;
            var version = match.Groups["v"].Value;
            var majorMatch = Regex.Match(version, @"^(\d+)");
            var major = majorMatch.Success ? int.Parse(majorMatch.Groups[1].Value) : 8;
            if (major == 1)
            {
                var legacy = Regex.Match(version, @"^1\.(\d+)");
                if (legacy.Success)
                    major = int.Parse(legacy.Groups[1].Value);
            }

            return new JavaInfo(path, major, version);
        }
        catch
        {
            return null;
        }
    }

    public static string? FindBest(int requiredMajor = 0)
    {
        var all = Scan();
        if (all.Count == 0)
            return null;
        if (requiredMajor > 0)
        {
            var exact = all.Where(x => x.Major >= requiredMajor).OrderBy(x => x.Major).FirstOrDefault();
            if (exact is not null)
                return exact.Path;
        }

        return all[0].Path;
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(string java, string arguments,
        string workingDir, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(java, arguments)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // ignored
            }

            throw;
        }

        return (process.ExitCode, output.ToString());

        void Collect(string? line)
        {
            if (line is null)
                return;
            output.AppendLine(line);
            onLine?.Invoke(line);
        }
    }
}
