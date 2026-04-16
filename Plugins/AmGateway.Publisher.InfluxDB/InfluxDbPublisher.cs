using System.Net;
using System.Text;
using AmGateway.Abstractions;
using AmGateway.Abstractions.Metadata;
using AmGateway.Abstractions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AmGateway.Publisher.InfluxDB;

/// <summary>
/// InfluxDB v2 北向发布器 — 使用 Line Protocol 通过 HTTP API 写入
/// 轻量实现：无第三方依赖，直接 POST Line Protocol 到 /api/v2/write
/// </summary>
[PublisherMetadata(
    Name = "InfluxDB",
    TransportName = "influxdb",
    Version = "1.0.0",
    Description = "InfluxDB v2 时序库发布器，使用 Line Protocol 写入")]
public sealed class InfluxDbPublisher : IPublisher
{
    private ILogger<InfluxDbPublisher> _logger = null!;
    private InfluxDbPublisherConfig _config = null!;
    private HttpClient? _httpClient;
    private string _writeUrl = string.Empty;

    // 批量缓冲
    private readonly List<string> _batchBuffer = [];
    private readonly object _batchLock = new();
    private Timer? _flushTimer;
    private int _batchCount;

    public string PublisherId { get; private set; } = string.Empty;

    public Task InitializeAsync(PublisherContext context, CancellationToken ct = default)
    {
        PublisherId = context.PublisherInstanceId;
        _logger = context.LoggerFactory.CreateLogger<InfluxDbPublisher>();

        _config = new InfluxDbPublisherConfig();
        context.Configuration.Bind(_config);

        _writeUrl = $"{_config.Url.TrimEnd('/')}/api/v2/write" +
                    $"?org={Uri.EscapeDataString(_config.Org)}" +
                    $"&bucket={Uri.EscapeDataString(_config.Bucket)}" +
                    $"&precision=ms";

        _logger.LogInformation("[{PublisherId}] InfluxDB 发布器已初始化 - {Url}, Org: {Org}, Bucket: {Bucket}, Measurement: {Measurement}",
            PublisherId, _config.Url, _config.Org, _config.Bucket, _config.Measurement);

        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Token {_config.Token}");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

        if (_config.GzipEncoding)
        {
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");
        }

        // 验证连接
        try
        {
            var healthUrl = $"{_config.Url.TrimEnd('/')}/health";
            var response = await _httpClient.GetAsync(healthUrl, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[{PublisherId}] InfluxDB 连接验证成功", PublisherId);
            }
            else
            {
                _logger.LogWarning("[{PublisherId}] InfluxDB 连接验证返回 {StatusCode}: {Reason}",
                    PublisherId, (int)response.StatusCode, response.ReasonPhrase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{PublisherId}] InfluxDB 连接验证失败，将在后续写入时重试", PublisherId);
        }

        // 启动定时刷新
        _flushTimer = new Timer(_ => FlushBatchAsync().GetAwaiter().GetResult(),
            null, TimeSpan.FromMilliseconds(_config.FlushIntervalMs), TimeSpan.FromMilliseconds(_config.FlushIntervalMs));

        _logger.LogInformation("[{PublisherId}] InfluxDB 发布器已启动 (BatchSize: {BatchSize}, FlushInterval: {FlushMs}ms)",
            PublisherId, _config.BatchSize, _config.FlushIntervalMs);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        // 停止定时器
        _flushTimer?.Dispose();
        _flushTimer = null;

        // 刷出剩余数据
        await FlushBatchAsync();

        _logger.LogInformation("[{PublisherId}] InfluxDB 发布器已停止，共写入 {Count} 批次",
            PublisherId, _batchCount);
    }

    public ValueTask PublishAsync(DataPoint point, CancellationToken ct = default)
    {
        var line = DataPointToLineProtocol(point);

        bool shouldFlush;
        lock (_batchLock)
        {
            _batchBuffer.Add(line);
            shouldFlush = _batchBuffer.Count >= _config.BatchSize;
        }

        if (shouldFlush)
        {
            return new ValueTask(FlushBatchAsync());
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask PublishBatchAsync(IReadOnlyList<DataPoint> points, CancellationToken ct = default)
    {
        var lines = new List<string>(points.Count);
        foreach (var point in points)
        {
            lines.Add(DataPointToLineProtocol(point));
        }

        bool shouldFlush;
        lock (_batchLock)
        {
            _batchBuffer.AddRange(lines);
            shouldFlush = _batchBuffer.Count >= _config.BatchSize;
        }

        if (shouldFlush)
        {
            await FlushBatchAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _httpClient?.Dispose();
    }

    /// <summary>
    /// 将缓冲区中的 Line Protocol 数据刷写到 InfluxDB
    /// </summary>
    private async Task FlushBatchAsync()
    {
        List<string> toFlush;
        lock (_batchLock)
        {
            if (_batchBuffer.Count == 0) return;
            toFlush = [.. _batchBuffer];
            _batchBuffer.Clear();
        }

        var payload = string.Join("\n", toFlush);

        try
        {
            if (_httpClient == null)
            {
                _logger.LogWarning("[{PublisherId}] HTTP 客户端未初始化，跳过 {Count} 条数据",
                    PublisherId, toFlush.Count);
                return;
            }

            var content = new StringContent(payload, Encoding.UTF8, "text/plain");
            var response = await _httpClient.PostAsync(_writeUrl, content);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent)
            {
                Interlocked.Increment(ref _batchCount);
                _logger.LogDebug("[{PublisherId}] InfluxDB 写入成功: {Count} 条 Line Protocol",
                    PublisherId, toFlush.Count);
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("[{PublisherId}] InfluxDB 写入失败: {StatusCode} - {Error}",
                    PublisherId, (int)response.StatusCode, errorBody);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[{PublisherId}] InfluxDB 写入异常（HTTP 连接问题）", PublisherId);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("[{PublisherId}] InfluxDB 写入超时", PublisherId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{PublisherId}] InfluxDB 写入未知异常", PublisherId);
        }
    }

    /// <summary>
    /// 将 DataPoint 转换为 InfluxDB Line Protocol 格式
    /// 
    /// 格式: measurement,tag1=val1,tag2=val2 field1=val1,field2=val2 timestamp
    /// 
    /// 映射规则:
    ///   measurement = 配置的 Measurement 名（默认 datapoints）
    ///   tags: source_driver, tag_name, quality
    ///   fields: value (数值或字符串)
    ///   timestamp: 毫秒精度
    /// </summary>
    private string DataPointToLineProtocol(DataPoint point)
    {
        var sb = new StringBuilder(256);

        // Measurement
        sb.Append(EscapeKey(_config.Measurement));

        // Tags（必须按 key 排序以优化 InfluxDB 压缩）
        sb.Append(',').Append("source_driver=").Append(EscapeTagValue(point.SourceDriver));
        sb.Append(',').Append("tag=").Append(EscapeTagValue(point.Tag));

        if (point.Quality != DataQuality.Good)
        {
            sb.Append(',').Append("quality=").Append(EscapeTagValue(point.Quality.ToString()));
        }

        // Fields（至少一个 field）
        sb.Append(' ');

        if (point.Value != null)
        {
            switch (point.Value)
            {
                case double d:
                    sb.Append("value=").Append(d.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case float f:
                    sb.Append("value=").Append(f.ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case int i:
                    sb.Append("value=").Append(i).Append('i');
                    break;
                case long l:
                    sb.Append("value=").Append(l).Append('i');
                    break;
                case bool b:
                    sb.Append("value=").Append(b ? "true" : "false");
                    break;
                case decimal dec:
                    sb.Append("value=").Append(dec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    // 字符串值需要引号
                    sb.Append("value=\"").Append(EscapeFieldValue(point.Value.ToString() ?? "")).Append('"');
                    break;
            }
        }
        else
        {
            sb.Append("value=null");
        }

        // 附加 metadata 为额外 fields
        if (point.Metadata != null && point.Metadata.Count > 0)
        {
            foreach (var kv in point.Metadata)
            {
                if (string.Equals(kv.Key, "value", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(',').Append(EscapeKey(kv.Key)).Append("=\"").Append(EscapeFieldValue(kv.Value?.ToString() ?? "")).Append('"');
            }
        }

        // Timestamp（毫秒精度）
        var unixMs = point.Timestamp.ToUnixTimeMilliseconds();
        sb.Append(' ').Append(unixMs);

        return sb.ToString();
    }

    /// <summary>转义 Measurement/Tag Key 中的特殊字符</summary>
    private static string EscapeKey(string key) =>
        key.Replace(" ", "\\ ")
           .Replace(",", "\\,")
           .Replace("=", "\\=");

    /// <summary>转义 Tag Value 中的特殊字符</summary>
    private static string EscapeTagValue(string value) =>
        value.Replace(" ", "\\ ")
             .Replace(",", "\\,")
             .Replace("=", "\\=");

    /// <summary>转义 Field Value（字符串）中的特殊字符</summary>
    private static string EscapeFieldValue(string value) =>
        value.Replace("\\", "\\\\")
             .Replace("\"", "\\\"")
             .Replace("\n", "\\n");
}
