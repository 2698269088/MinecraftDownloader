using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;

namespace MinecraftDownloader.Core;

public static class HttpService
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BodyIdleTimeout = TimeSpan.FromSeconds(60);

    private sealed class MirrorState
    {
        public int Failures;
        public DateTimeOffset CoolUntil;
    }

    private static readonly ConcurrentDictionary<string, MirrorState> MirrorStates =
        new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 64,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftDownloader/1.0");
        return client;
    }

    public static string Mirror(string url)
    {
        if (!LauncherConfig.Current.UseMirror)
            return url;

        return url
            .Replace("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
            .Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
            .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
            .Replace("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
            .Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
            .Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetCandidates(string url)
    {
        var mirrored = Mirror(url);
        var isMirrored = !string.Equals(mirrored, url, StringComparison.OrdinalIgnoreCase);
        if (isMirrored && !IsMirrorCooling(mirrored))
            yield return mirrored;
        yield return url;
    }

    private static bool IsMirrorCooling(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return MirrorStates.TryGetValue(uri.Host, out var state) &&
               state.CoolUntil > DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 镜像连续失败（超时、空响应、限流）后临时熔断一段时间，避免每个文件都在镜像上白等。
    /// </summary>
    private static void MarkMirrorFailure(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;
        var state = MirrorStates.GetOrAdd(uri.Host, _ => new MirrorState());
        lock (state)
        {
            state.Failures++;
            if (state.Failures >= 3)
            {
                state.CoolUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                state.Failures = 0;
                LogService.Warn($"镜像 {uri.Host} 连续失败，已临时改为直连官方源");
            }
        }
    }

    private static void MarkMirrorSuccess(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;
        if (MirrorStates.TryGetValue(uri.Host, out var state))
        {
            lock (state)
                state.Failures = 0;
        }
    }

    /// <summary>建立连接并拿到响应头；超时后抛 TimeoutException，便于回退到官方源。</summary>
    private static async Task<HttpResponseMessage> GetWithHeaderTimeoutAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HeaderTimeout);
        try
        {
            return await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"连接或响应超时：{url}");
        }
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var candidate in GetCandidates(url))
        {
            var isMirrored = !string.Equals(candidate, url, StringComparison.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var response = await GetWithHeaderTimeoutAsync(candidate, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    bodyCts.CancelAfter(BodyIdleTimeout);
                    var text = await response.Content.ReadAsStringAsync(bodyCts.Token).ConfigureAwait(false);
                    if (text.Length == 0)
                        throw new InvalidDataException("响应内容为空");
                    if (isMirrored)
                        MarkMirrorSuccess(candidate);
                    return text;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    last = new TimeoutException($"读取响应超时：{candidate}");
                    if (isMirrored)
                        MarkMirrorFailure(candidate);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (isMirrored)
                        MarkMirrorFailure(candidate);
                }
            }
        }

        throw new HttpRequestException($"请求失败：{url}", last);
    }

    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var candidate in GetCandidates(url))
        {
            var isMirrored = !string.Equals(candidate, url, StringComparison.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var response = await GetWithHeaderTimeoutAsync(candidate, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    bodyCts.CancelAfter(BodyIdleTimeout);
                    var bytes = await response.Content.ReadAsByteArrayAsync(bodyCts.Token).ConfigureAwait(false);
                    if (bytes.Length == 0)
                        throw new InvalidDataException("响应内容为空");
                    if (isMirrored)
                        MarkMirrorSuccess(candidate);
                    return bytes;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    last = new TimeoutException($"读取响应超时：{candidate}");
                    if (isMirrored)
                        MarkMirrorFailure(candidate);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (isMirrored)
                        MarkMirrorFailure(candidate);
                }
            }
        }

        throw new HttpRequestException($"请求失败：{url}", last);
    }

    public static async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        var text = await GetStringAsync(url, ct).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Deserialize<T>(text, JsonDefaults.Options);
    }

    public static async Task<string> PostJsonAsync(string url, string jsonBody, CancellationToken ct = default)
    {
        using var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(HeaderTimeout);
        using var response = await Client.PostAsync(url, content, headerCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyCts.CancelAfter(BodyIdleTimeout);
        return await response.Content.ReadAsStringAsync(bodyCts.Token).ConfigureAwait(false);
    }

    public static async Task DownloadFileAsync(string url, string path, string? sha1,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        TryDelete(temp);
        Exception? last = null;

        foreach (var candidate in GetCandidates(url))
        {
            var isMirrored = !string.Equals(candidate, url, StringComparison.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var response = await GetWithHeaderTimeoutAsync(candidate, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? 0;
                    long done = 0;

                    await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write,
                                     FileShare.None, 1024 * 64, true))
                    {
                        var buffer = new byte[1024 * 64];
                        int read;
                        while ((read = await ReadWithIdleTimeoutAsync(source, buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            done += read;
                            progress?.Report((done, total));
                        }
                    }

                    if (done == 0)
                        throw new InvalidDataException($"下载内容为空：{Path.GetFileName(path)}");

                    if (!string.IsNullOrEmpty(sha1))
                    {
                        var actual = await HashService.Sha1FileAsync(temp, ct).ConfigureAwait(false);
                        if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"文件校验失败（SHA1 不匹配）：{Path.GetFileName(path)}");
                    }

                    if (File.Exists(path))
                        File.Delete(path);
                    File.Move(temp, path);
                    if (isMirrored)
                        MarkMirrorSuccess(candidate);
                    return;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    last = new TimeoutException($"下载超时：{candidate}");
                    TryDelete(temp);
                    if (isMirrored)
                        MarkMirrorFailure(candidate);
                }
                catch (OperationCanceledException)
                {
                    TryDelete(temp);
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    TryDelete(temp);
                    if (isMirrored)
                        MarkMirrorFailure(candidate);
                }
            }
        }

        throw new IOException($"下载失败：{url}", last);
    }

    private static async Task<int> ReadWithIdleTimeoutAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BodyIdleTimeout);
        try
        {
            return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("下载数据超时");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }
}

public static class JsonDefaults
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };
}
