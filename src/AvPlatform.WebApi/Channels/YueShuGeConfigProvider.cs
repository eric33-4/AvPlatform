using System.Diagnostics;
using System.Text.Json;

namespace AvPlatform.WebApi.Channels;

/// <summary>读取并缓存阅姝阁中央配置中的动态节点和短期令牌。</summary>
public sealed class YueShuGeConfigProvider(
    IConfiguration configuration,
    ILogger<YueShuGeConfigProvider> logger)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private YueShuGeConfigSnapshot? _snapshot;

    public async Task<string?> GetHostAsync(
        string name,
        CancellationToken cancellationToken) =>
        (await GetSnapshotAsync(false, cancellationToken)).Hosts.GetValueOrDefault(name);

    public async Task<string?> GetTokenAsync(
        string name,
        CancellationToken cancellationToken) =>
        (await GetSnapshotAsync(false, cancellationToken)).Tokens.GetValueOrDefault(name);

    public async Task<YueShuGeConfigSnapshot> GetSnapshotAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var refreshMinutes = configuration.GetValue("Channels:Box:RefreshMinutes", 15);
        if (!forceRefresh && _snapshot is { } cached &&
            cached.FetchedAt.AddMinutes(refreshMinutes) > DateTimeOffset.UtcNow)
        {
            return cached;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _snapshot is { } doubleChecked &&
                doubleChecked.FetchedAt.AddMinutes(refreshMinutes) > DateTimeOffset.UtcNow)
            {
                return doubleChecked;
            }

            try
            {
                _snapshot = await LoadAsync(cancellationToken);
                return _snapshot;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && _snapshot is not null)
            {
                logger.LogWarning(exception, "阅姝阁中央配置刷新失败，继续使用上次成功结果。");
                return _snapshot;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<YueShuGeConfigSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var endpoints = configuration.GetSection("Channels:Box:Endpoints").Get<string[]>() ??
        [
            "http://38.46.10.2:9672/",
            "http://38.46.10.3:9672/",
            "http://38.46.10.4:9672/",
            "http://38.46.10.5:9672/",
            "http://38.46.10.6:9672/"
        ];

        Exception? lastError = null;
        foreach (var endpoint in endpoints)
        {
            var stopwatch = Stopwatch.StartNew();
            var statusCode = 0;
            try
            {
                var requestUri = new Uri(new Uri(EnsureTrailingSlash(endpoint)), "box/api/config");
                var response = await PostWithCurlAsync(
                    requestUri,
                    configuration.GetValue("Channels:Box:Channel", "vjc"),
                    cancellationToken);
                statusCode = response.StatusCode;
                var snapshot = Parse(response.Body);

                logger.LogInformation(
                    "阅姝阁中央配置刷新成功：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    requestUri.Host,
                    statusCode,
                    stopwatch.ElapsedMilliseconds);
                return snapshot;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
                logger.LogWarning(
                    exception,
                    "阅姝阁中央配置节点失败：{Host}，状态码：{StatusCode}，耗时：{ElapsedMilliseconds}ms",
                    Host(endpoint),
                    statusCode == 0 ? null : statusCode,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        throw new HttpRequestException("所有阅姝阁中央配置节点均调用失败。", lastError);
    }

    private static YueShuGeConfigSnapshot Parse(byte[] encrypted)
    {
        using var document = YueShuGeBoxCodec.Decrypt(encrypted);
        var data = document.RootElement.GetProperty("data");
        return new YueShuGeConfigSnapshot(
            ReadMap(data.GetProperty("api"), "name", "host"),
            ReadMap(data.GetProperty("token"), "name", "token"),
            DateTimeOffset.UtcNow);
    }

    private static Dictionary<string, string> ReadMap(
        JsonElement items,
        string keyProperty,
        string valueProperty)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            var key = item.TryGetProperty(keyProperty, out var keyElement) ? keyElement.GetString() : null;
            var value = item.TryGetProperty(valueProperty, out var valueElement) ? valueElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                result[key] = value;
            }
        }
        return result;
    }

    private static async Task<CurlResponse> PostWithCurlAsync(
        Uri requestUri,
        string channel,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "curl.exe" : "curl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--show-error");
        startInfo.ArgumentList.Add("--location");
        startInfo.ArgumentList.Add("--max-time");
        startInfo.ArgumentList.Add("20");
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add("POST");
        startInfo.ArgumentList.Add("--data-urlencode");
        startInfo.ArgumentList.Add($"channel={channel}");
        startInfo.ArgumentList.Add("--write-out");
        startInfo.ArgumentList.Add("%{stderr}\nAVP_STATUS:%{http_code}\n");
        startInfo.ArgumentList.Add(requestUri.ToString());

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        await using var body = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(body, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await Task.WhenAll(copyTask, process.WaitForExitAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }

        var stderr = await errorTask;
        var marker = stderr.LastIndexOf("AVP_STATUS:", StringComparison.Ordinal);
        var statusText = marker < 0
            ? null
            : stderr[(marker + "AVP_STATUS:".Length)..].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
        if (process.ExitCode != 0 || !int.TryParse(statusText, out var statusCode) || statusCode is < 200 or >= 300)
        {
            throw new HttpRequestException(
                $"curl 中央配置请求失败，退出码 {process.ExitCode}，HTTP {statusText ?? "unknown"}。");
        }

        return new CurlResponse(statusCode, body.ToArray());
    }

    private static string EnsureTrailingSlash(string endpoint) =>
        endpoint.EndsWith("/", StringComparison.Ordinal) ? endpoint : endpoint + "/";

    private static string Host(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "invalid-endpoint";

    private sealed record CurlResponse(int StatusCode, byte[] Body);
}

public sealed record YueShuGeConfigSnapshot(
    IReadOnlyDictionary<string, string> Hosts,
    IReadOnlyDictionary<string, string> Tokens,
    DateTimeOffset FetchedAt);
