using System.Collections.Concurrent;
using System.Text;

namespace AmGateway.Services;

/// <summary>
/// 网关指标采集器 — 线程安全，Prometheus text format 输出
/// </summary>
public sealed class GatewayMetrics
{
    // === 计数器 ===
    private long _dataPointsReceived;
    private long _dataPointsPublished;
    private long _dataPointsFiltered;
    private long _dataPointsDropped;
    private long _publishErrors;

    // === 仪表盘 ===
    private int _channelCount;
    private int _channelCapacity = 10000;
    private int _activeDrivers;
    private int _activePublishers;

    // === 直方图（简化版：记录最近处理耗时） ===
    private readonly ConcurrentQueue<double> _recentLatencyMs = new();
    private const int MaxLatencySamples = 1000;

    // === 运行时间 ===
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    // --- 计数器操作 ---

    public void DataPointReceived() => Interlocked.Increment(ref _dataPointsReceived);
    public void DataPointPublished() => Interlocked.Increment(ref _dataPointsPublished);
    public void DataPointFiltered() => Interlocked.Increment(ref _dataPointsFiltered);
    public void DataPointDropped() => Interlocked.Increment(ref _dataPointsDropped);
    public void PublishError() => Interlocked.Increment(ref _publishErrors);

    // --- 仪表盘操作 ---

    public void SetChannelCount(int count) => Interlocked.Exchange(ref _channelCount, count);
    public void SetChannelCapacity(int capacity) => Interlocked.Exchange(ref _channelCapacity, capacity);
    public void SetActiveDrivers(int count) => Interlocked.Exchange(ref _activeDrivers, count);
    public void SetActivePublishers(int count) => Interlocked.Exchange(ref _activePublishers, count);

    // --- 延迟采样 ---

    public void RecordLatency(TimeSpan elapsed)
    {
        _recentLatencyMs.Enqueue(elapsed.TotalMilliseconds);
        while (_recentLatencyMs.Count > MaxLatencySamples)
            _recentLatencyMs.TryDequeue(out _);
    }

    // --- Prometheus text format 输出 ---

    public string ExportPrometheus()
    {
        var sb = new StringBuilder(2048);
        var uptime = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds;

        // 帮助信息 + 类型 + 值
        WriteCounter(sb, "amgateway_data_points_received_total", "Total data points received from drivers", _dataPointsReceived);
        WriteCounter(sb, "amgateway_data_points_published_total", "Total data points published to north-bound", _dataPointsPublished);
        WriteCounter(sb, "amgateway_data_points_filtered_total", "Total data points filtered by transform rules", _dataPointsFiltered);
        WriteCounter(sb, "amgateway_data_points_dropped_total", "Total data points dropped (channel full)", _dataPointsDropped);
        WriteCounter(sb, "amgateway_publish_errors_total", "Total publish errors", _publishErrors);

        WriteGauge(sb, "amgateway_channel_items_count", "Current items in channel", _channelCount);
        WriteGauge(sb, "amgateway_channel_capacity", "Channel capacity", _channelCapacity);
        WriteGauge(sb, "amgateway_active_drivers", "Number of active drivers", _activeDrivers);
        WriteGauge(sb, "amgateway_active_publishers", "Number of active publishers", _activePublishers);
        WriteGauge(sb, "amgateway_uptime_seconds", "Gateway uptime in seconds", uptime);

        // 延迟统计
        var latencies = _recentLatencyMs.ToArray();
        if (latencies.Length > 0)
        {
            Array.Sort(latencies);
            var avg = latencies.Average();
            var p50 = Percentile(latencies, 0.50);
            var p95 = Percentile(latencies, 0.95);
            var p99 = Percentile(latencies, 0.99);
            var max = latencies[^1];

            WriteGauge(sb, "amgateway_pipeline_latency_ms_avg", "Average pipeline latency (ms)", avg);
            WriteGauge(sb, "amgateway_pipeline_latency_ms_p50", "P50 pipeline latency (ms)", p50);
            WriteGauge(sb, "amgateway_pipeline_latency_ms_p95", "P95 pipeline latency (ms)", p95);
            WriteGauge(sb, "amgateway_pipeline_latency_ms_p99", "P99 pipeline latency (ms)", p99);
            WriteGauge(sb, "amgateway_pipeline_latency_ms_max", "Max pipeline latency (ms)", max);
        }

        // 每秒吞吐量（基于运行时间）
        if (uptime > 0)
        {
            WriteGauge(sb, "amgateway_throughput_points_per_second", "Data points per second (lifetime avg)",
                _dataPointsReceived / uptime);
        }

        return sb.ToString();
    }

    /// <summary>获取指标快照（JSON 格式，用于 /api/health 增强）</summary>
    public Dictionary<string, object> GetSnapshot() => new()
    {
        ["uptimeSeconds"] = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
        ["dataPointsReceived"] = Interlocked.Read(ref _dataPointsReceived),
        ["dataPointsPublished"] = Interlocked.Read(ref _dataPointsPublished),
        ["dataPointsFiltered"] = Interlocked.Read(ref _dataPointsFiltered),
        ["dataPointsDropped"] = Interlocked.Read(ref _dataPointsDropped),
        ["publishErrors"] = Interlocked.Read(ref _publishErrors),
        ["channelItems"] = _channelCount,
        ["channelCapacity"] = _channelCapacity,
        ["activeDrivers"] = _activeDrivers,
        ["activePublishers"] = _activePublishers,
    };

    private static void WriteCounter(StringBuilder sb, string name, string help, long value)
    {
        sb.AppendLine($"# HELP {name} {help}");
        sb.AppendLine($"# TYPE {name} counter");
        sb.AppendLine($"{name} {value}");
    }

    private static void WriteGauge(StringBuilder sb, string name, string help, double value)
    {
        sb.AppendLine($"# HELP {name} {help}");
        sb.AppendLine($"# TYPE {name} gauge");
        sb.AppendLine($"{name} {value:G}");
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Length - 1))];
    }
}
