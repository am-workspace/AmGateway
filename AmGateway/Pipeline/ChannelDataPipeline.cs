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
    /// <summary>有界 Channel：驱动生产端写入，消费者异步读取，容量 10000</summary>
    private readonly Channel<DataPoint> _channel;
    private readonly ILogger<ChannelDataPipeline> _logger;
    private readonly GatewayMetrics _metrics;

    /// <summary>Channel 写入端封装，供驱动通过 IDataSink 接口非阻塞写入</summary>
    private readonly ChannelDataSink _sink;

    /// <summary>已注册的北向发布器列表，运行时支持动态增删</summary>
    private readonly List<IPublisher> _publishers = [];
    private readonly object _publishersLock = new();

    /// <summary>可选：转换引擎（Transform），对数据点做预处理/过滤/计算</summary>
    private ITransformEngine? _transformEngine;

    /// <summary>可选：路由解析器（Route），决定数据点分发到哪些发布器</summary>
    private IRouteResolver? _routeResolver;

    private Task? _consumeTask;
    private CancellationTokenSource? _cts;

    public ChannelDataPipeline(ILogger<ChannelDataPipeline> logger, GatewayMetrics metrics)
    {
        _logger = logger;
        _metrics = metrics;

        // 创建有界 Channel：容量 10000，满时丢弃最旧数据
        // SingleReader=true 优化性能（只有一个消费线程）
        // SingleWriter=false 允许多个驱动并发写入
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

    /// <summary>初始化时批量注册发布器（替换旧列表）</summary>
    public void SetPublishers(IReadOnlyList<IPublisher> publishers)
    {
        lock (_publishersLock)
        {
            _publishers.Clear();
            _publishers.AddRange(publishers);
        }
        _logger.LogInformation("[Pipeline] 已注册 {Count} 个北向发布器", _publishers.Count);
    }

    /// <summary>运行时动态添加发布器（热插拔）</summary>
    public void AddPublisher(IPublisher publisher)
    {
        lock (_publishersLock)
        {
            _publishers.Add(publisher);
        }
        _logger.LogInformation("[Pipeline] 动态添加发布器 {PublisherId}，当前共 {Count} 个",
            publisher.PublisherId, _publishers.Count);
    }

    /// <summary>运行时动态移除发布器（热插拔）</summary>
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

    /// <summary>注入转换引擎（可选中间件，在消费阶段执行）</summary>
    public void SetTransformEngine(ITransformEngine engine)
    {
        _transformEngine = engine;
        _logger.LogInformation("[Pipeline] 已设置转换引擎");
    }

    /// <summary>注入路由解析器（可选中间件，决定数据点分发目标）</summary>
    public void SetRouteResolver(IRouteResolver resolver)
    {
        _routeResolver = resolver;
        _logger.LogInformation("[Pipeline] 已设置路由解析器");
    }

    /// <summary>启动 Channel 消费循环（非阻塞，后台执行）</summary>
    public Task StartConsumingAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _consumeTask = ConsumeLoopAsync(_cts.Token);
        _logger.LogInformation("[Pipeline] 数据管道消费者已启动");
        return Task.CompletedTask;
    }

    /// <summary>优雅停止：取消 Token → 等待消费循环退出</summary>
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
                // 正常取消，预期行为
            }
        }

        _logger.LogInformation("[Pipeline] 数据管道消费者已停止");
    }

    /// <summary>
    /// Channel 消费主循环 — 单线程串行处理，保证数据点顺序性
    ///
    /// 处理流水线：
    ///   1. 转换（Transform）：数据预处理/过滤
    ///   2. 快照（Snapshot）：加锁拷贝发布器列表，避免发布器动态变化时竞态
    ///   3. 路由（Route）：决定分发到哪些发布器
    ///   4. 扇出（Fan-out）：逐个调用发布器，单点失败不影响整体
    ///   5. 指标（Metrics）：记录端到端延迟
    /// </summary>
    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        await foreach (var point in _channel.Reader.ReadAllAsync(ct))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // === 阶段 1：转换 ===
            // 若配置了转换引擎，对原始数据点做计算/映射/过滤
            DataPoint? transformed;
            if (_transformEngine != null)
            {
                transformed = _transformEngine.Transform(point);
                if (transformed == null)
                {
                    // 被转换规则过滤丢弃（如条件匹配不满足）
                    _metrics.DataPointFiltered();
                    continue;
                }
            }
            else
            {
                transformed = point;
            }

            // === 阶段 2：快照发布器列表 ===
            // 加锁拷贝，避免消费期间发布器被动态增删导致枚举异常
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

            // === 阶段 3：路由解析 ===
            // 若配置了路由规则，按规则筛选目标发布器；否则全部扇出
            IReadOnlyList<IPublisher> targets;
            if (_routeResolver != null)
            {
                targets = _routeResolver.Resolve(transformed.Value, snapshot);
            }
            else
            {
                targets = snapshot; // 无路由规则时扇出给所有发布器
            }

            // === 阶段 4：扇出分发 ===
            // 逐个发布，单个发布器异常被捕获，不影响其他发布器
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

            // === 阶段 5：记录延迟 ===
            // 从 Channel 消费到所有目标发布器完成发布的总耗时
            sw.Stop();
            _metrics.RecordLatency(sw.Elapsed);
        }
    }

    /// <summary>
    /// Channel 写入端的 IDataSink 实现
    /// 供驱动端（南向）通过 IDataSink 接口非阻塞地写入数据点到 Channel
    /// 写满时丢弃最旧数据（由 BoundedChannelOptions.FullMode 控制）
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

        /// <summary>单点写入：非阻塞，Channel 满时直接丢弃</summary>
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

        /// <summary>批量写入：逐条尝试，满时丢弃溢出部分</summary>
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
