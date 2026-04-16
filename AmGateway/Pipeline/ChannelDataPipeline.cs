using AmGateway.Abstractions;
using AmGateway.Abstractions.Models;
using AmGateway.Services;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AmGateway.Pipeline;

/// <summary>
/// 基于 Channel 的数据管道实现
/// 有界容量 10000，满时丢弃最旧数据（工业场景典型策略）
/// 消费者将 DataPoint 经过转换引擎 → 路由解析 → 分发给北向发布器
/// </summary>
public sealed class ChannelDataPipeline : IDataPipeline
{
    private readonly Channel<DataPoint> _channel;
    private readonly ILogger<ChannelDataPipeline> _logger;
    private readonly GatewayMetrics _metrics;
    private readonly ChannelDataSink _sink;
    private readonly List<IPublisher> _publishers = [];
    private readonly object _publishersLock = new();
    private ITransformEngine? _transformEngine;
    private IRouteResolver? _routeResolver;
    private Task? _consumeTask;
    private CancellationTokenSource? _cts;

    public ChannelDataPipeline(ILogger<ChannelDataPipeline> logger, GatewayMetrics metrics)
    {
        _logger = logger;
        _metrics = metrics;
        _channel = Channel.CreateBounded<DataPoint>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _sink = new ChannelDataSink(_channel.Writer, logger, metrics);
    }

    public IDataSink Writer => _sink;

    public int PendingCount => _channel.Reader.Count;

    public void SetPublishers(IReadOnlyList<IPublisher> publishers)
    {
        lock (_publishersLock)
        {
            _publishers.Clear();
            _publishers.AddRange(publishers);
        }
        _logger.LogInformation("[Pipeline] 已注册 {Count} 个北向发布器", _publishers.Count);
    }

    public void AddPublisher(IPublisher publisher)
    {
        lock (_publishersLock)
        {
            _publishers.Add(publisher);
        }
        _logger.LogInformation("[Pipeline] 动态添加发布器 {PublisherId}，当前共 {Count} 个",
            publisher.PublisherId, _publishers.Count);
    }

    public bool RemovePublisher(IPublisher publisher)
    {
        lock (_publishersLock)
        {
            var removed = _publishers.Remove(publisher);
            if (removed)
            {
                _logger.LogInformation("[Pipeline] 动态移除发布器 {PublisherId}，当前共 {Count} 个",
                    publisher.PublisherId, _publishers.Count);
            }
            return removed;
        }
    }

    public void SetTransformEngine(ITransformEngine engine)
    {
        _transformEngine = engine;
        _logger.LogInformation("[Pipeline] 已设置转换引擎");
    }

    public void SetRouteResolver(IRouteResolver resolver)
    {
        _routeResolver = resolver;
        _logger.LogInformation("[Pipeline] 已设置路由解析器");
    }

    public Task StartConsumingAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _consumeTask = ConsumeLoopAsync(_cts.Token);
        _logger.LogInformation("[Pipeline] 数据管道消费者已启动");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
        }

        if (_consumeTask != null)
        {
            try
            {
                await _consumeTask;
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
        }

        _logger.LogInformation("[Pipeline] 数据管道消费者已停止");
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        await foreach (var point in _channel.Reader.ReadAllAsync(ct))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 1. 转换阶段
            DataPoint? transformed;
            if (_transformEngine != null)
            {
                transformed = _transformEngine.Transform(point);
                if (transformed == null)
                {
                    // 被过滤丢弃
                    _metrics.DataPointFiltered();
                    continue;
                }
            }
            else
            {
                transformed = point;
            }

            // 2. 快照读取当前发布器列表
            List<IPublisher> snapshot;
            lock (_publishersLock)
            {
                snapshot = _publishers.Count > 0 ? [.. _publishers] : [];
            }

            if (snapshot.Count == 0)
            {
                // 无发布器时仅记录日志（向后兼容 Phase 1 行为）
                _logger.LogDebug("[Pipeline] DataPoint: {Tag} = {Value} [{Quality}] from {Source} at {Timestamp}",
                    transformed.Value.Tag, transformed.Value.Value, transformed.Value.Quality, transformed.Value.SourceDriver, transformed.Value.Timestamp);
                continue;
            }

            // 3. 路由阶段：决定发到哪些发布器
            IReadOnlyList<IPublisher> targets;
            if (_routeResolver != null)
            {
                targets = _routeResolver.Resolve(transformed.Value, snapshot);
            }
            else
            {
                targets = snapshot; // 无路由规则时扇出给所有发布器
            }

            // 4. 扇出：分发给目标发布器，单个发布器异常不影响其他
            foreach (var publisher in targets)
            {
                try
                {
                    await publisher.PublishAsync(transformed.Value, ct);
                    _metrics.DataPointPublished();
                }
                catch (Exception ex)
                {
                    _metrics.PublishError();
                    _logger.LogError(ex, "[Pipeline] 发布器 {PublisherId} 处理数据点失败: {Tag}",
                        publisher.PublisherId, transformed.Value.Tag);
                }
            }

            // 5. 记录延迟
            sw.Stop();
            _metrics.RecordLatency(sw.Elapsed);
        }
    }

    /// <summary>
    /// Channel 写入端的 IDataSink 实现
    /// </summary>
    private sealed class ChannelDataSink : IDataSink
    {
        private readonly ChannelWriter<DataPoint> _writer;
        private readonly ILogger _logger;
        private readonly GatewayMetrics _metrics;

        public ChannelDataSink(ChannelWriter<DataPoint> writer, ILogger logger, GatewayMetrics metrics)
        {
            _writer = writer;
            _logger = logger;
            _metrics = metrics;
        }

        public ValueTask PublishAsync(DataPoint point, CancellationToken ct = default)
        {
            if (_writer.TryWrite(point))
            {
                _metrics.DataPointReceived();
                return ValueTask.CompletedTask;
            }

            _metrics.DataPointDropped();
            _logger.LogWarning("[Pipeline] Channel 已满，数据点被丢弃: {Tag}", point.Tag);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishBatchAsync(IReadOnlyList<DataPoint> points, CancellationToken ct = default)
        {
            foreach (var point in points)
            {
                if (!_writer.TryWrite(point))
                {
                    _metrics.DataPointDropped();
                    _logger.LogWarning("[Pipeline] Channel 已满，批量数据点被丢弃: {Tag}", point.Tag);
                }
                else
                {
                    _metrics.DataPointReceived();
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
