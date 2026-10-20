using System.IO;
using System.Security.Cryptography;

namespace MinecraftDownloader.Core;

public static class HashService
{
    public static async Task<string> Sha1FileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
        using var sha1 = SHA1.Create();
        var hash = await sha1.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<string> Sha512FileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
        using var sha512 = SHA512.Create();
        var hash = await sha512.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>安装过程中上报给界面的进度。</summary>
public sealed class InstallProgress
{
    public string Stage { get; init; } = "";
    public string Detail { get; init; } = "";
    public double? Percent { get; init; }
    public long DoneBytes { get; init; }
    public long TotalBytes { get; init; }

    public static InstallProgress Status(string stage, string detail = "", double? percent = null)
        => new() { Stage = stage, Detail = detail, Percent = percent };
}
