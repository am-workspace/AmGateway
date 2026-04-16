using System.Diagnostics;
using System.Diagnostics.Metrics;
using AmGateway.Abstractions;
using AmGateway.Abstractions.Metadata;
using AmGateway.Abstractions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace AmGateway.Driver.OpcUa;

/// <summary>
/// ITelemetryContext 的最小空实现，用于 OPC UA SDK
/// </summary>
file sealed class NullTelemetryContext : ITelemetryContext
{
    private static readonly NullLoggerFactory _nullLoggerFactory = new();

    public ILoggerFactory LoggerFactory => _nullLoggerFactory;
    public ActivitySource ActivitySource { get; } = new("AmGateway.OpcUa");
    public System.Diagnostics.Metrics.Meter CreateMeter() => new("AmGateway.OpcUa");
}

/// <summary>
/// 空的 LoggerFactory 实现
/// </summary>
file sealed class NullLoggerFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}

/// <summary>
/// 空的 Logger 实现
/// </summary>
file class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

/// <summary>
/// OPC UA 协议驱动 - 订阅 OPC UA Server 节点变化
/// 核心通信逻辑复用自旧项目 PlcGateway/Services/OpcUaClientService.cs
/// </summary>
[DriverMetadata(
    Name = "OPC UA",
    ProtocolName = "opcua",
    Version = "1.0.0",
    Description = "OPC UA 协议驱动，支持订阅模式监控节点值变化")]
public sealed class OpcUaDriver : IProtocolDriver
{
    private ILogger<OpcUaDriver> _logger = null!;
    private IDataSink _dataSink = null!;
    private OpcUaDriverConfig _config = null!;
    private Session? _session;
    private Subscription? _subscription;

    public string DriverId { get; private set; } = string.Empty;

    public Task InitializeAsync(DriverContext context, CancellationToken ct = default)
    {
        DriverId = context.DriverInstanceId;
        _logger = context.LoggerFactory.CreateLogger<OpcUaDriver>();
        _dataSink = context.DataSink;

        _config = new OpcUaDriverConfig();
        context.Configuration.Bind(_config);

        _logger.LogInformation("[{DriverId}] OPC UA 驱动已初始化 - 端点: {Endpoint}, 节点数: {Count}",
            DriverId, _config.Endpoint, _config.Nodes.Count);

        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            await ConnectAsync(ct);
            await CreateSubscriptionAsync();
            _logger.LogInformation("[{DriverId}] OPC UA 驱动已启动，已连接到 {Endpoint}", DriverId, _config.Endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{DriverId}] OPC UA 启动失败", DriverId);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_subscription != null)
        {
            await _subscription.DeleteAsync(true);
            _subscription = null;
        }

        if (_session != null)
        {
            await _session.CloseAsync();
            _session.Dispose();
            _session = null;
        }

        _logger.LogInformation("[{DriverId}] OPC UA 驱动已停止", DriverId);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    /// <summary>
    /// 连接到 OPC UA Server（复用旧项目 ConnectAsync 逻辑）
    /// </summary>
    private async Task ConnectAsync(CancellationToken ct)
    {
        var config = new ApplicationConfiguration
        {
            ApplicationName = "AmGateway",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                AutoAcceptUntrustedCertificates = true
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = 60000
            }
        };

        // 发现端点
        using var discoveryClient = await DiscoveryClient.CreateAsync(config, new Uri(_config.Endpoint), DiagnosticsMasks.None, ct);
        var endpoints = await discoveryClient.GetEndpointsAsync(null, ct);

        var endpointDescription = endpoints.FirstOrDefault(e => e.SecurityPolicyUri == SecurityPolicies.None)
            ?? endpoints.First();

        var endpointConfiguration = EndpointConfiguration.Create(config);
        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

        var sessionFactory = new DefaultSessionFactory(new NullTelemetryContext());
        _session = (Session)await sessionFactory.CreateAsync(
            config,
            endpoint,
            false,
            "AmGateway",
            60000,
            new UserIdentity(new AnonymousIdentityToken()),
            null,
            ct);

        _logger.LogInformation("[{DriverId}] OPC UA 会话已建立", DriverId);
    }

    /// <summary>
    /// 创建订阅和监控项（复用旧项目 CreateSubscription 逻辑，适配 IDataSink）
    /// </summary>
    private async Task CreateSubscriptionAsync()
    {
        if (_session == null) return;

        _subscription = new Subscription(_session.DefaultSubscription)
        {
            PublishingInterval = _config.PublishingIntervalMs,
            DisplayName = $"AmGateway_{DriverId}"
        };

        foreach (var node in _config.Nodes)
        {
            var monitoredItem = new MonitoredItem(_subscription.DefaultItem)
            {
                StartNodeId = node.NodeId,
                AttributeId = Attributes.Value,
                DisplayName = node.Name,
                SamplingInterval = _config.SamplingIntervalMs,
                QueueSize = 10
            };

            monitoredItem.Notification += OnMonitoredItemNotification;
            _subscription.AddItem(monitoredItem);
        }

        _session.AddSubscription(_subscription);
        await _subscription.CreateAsync();
        await _subscription.ApplyChangesAsync();

        // 检查 MonitoredItem 是否创建成功
        foreach (var item in _subscription.MonitoredItems)
        {
            if (!item.Created)
            {
                _logger.LogError("[{DriverId}] MonitoredItem 创建失败: NodeId={NodeId}, Status={Status}",
                    DriverId, item.StartNodeId, item.Status);
            }
            else
            {
                _logger.LogDebug("[{DriverId}] MonitoredItem 已创建: NodeId={NodeId}, ClientHandle={ClientHandle}",
                    DriverId, item.StartNodeId, item.ClientHandle);
            }
        }

        _logger.LogInformation("[{DriverId}] OPC UA 订阅已创建，监控 {Count} 个节点, PublishingInterval: {Interval}ms",
            DriverId, _config.Nodes.Count, _subscription.PublishingInterval);
    }

    /// <summary>
    /// MonitoredItem 通知回调（复用旧项目回调逻辑，适配 DataPoint 输出）
    /// </summary>
    private async void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        try
        {
            object? value = null;
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            DataQuality quality = DataQuality.Good;

            if (e.NotificationValue is MonitoredItemNotification notification && notification.Value != null)
            {
                value = notification.Value.Value;
                timestamp = notification.Value.SourceTimestamp != DateTime.MinValue
                    ? new DateTimeOffset(notification.Value.SourceTimestamp, TimeSpan.Zero)
                    : DateTimeOffset.UtcNow;
                quality = MapStatusCode(notification.Value.StatusCode);
            }
            else if (e.NotificationValue is DataValue dataValue && dataValue.Value != null)
            {
                value = dataValue.Value;
                timestamp = dataValue.SourceTimestamp != DateTime.MinValue
                    ? new DateTimeOffset(dataValue.SourceTimestamp, TimeSpan.Zero)
                    : DateTimeOffset.UtcNow;
                quality = MapStatusCode(dataValue.StatusCode);
            }
            else
            {
                quality = DataQuality.Bad;
            }

            await _dataSink.PublishAsync(new DataPoint
            {
                Tag = $"{DriverId}/{item.DisplayName}",
                Value = value,
                Timestamp = timestamp,
                Quality = quality,
                SourceDriver = DriverId,
                Metadata = new Dictionary<string, string>
                {
                    ["nodeId"] = item.StartNodeId.ToString(),
                    ["protocol"] = "opcua"
                }
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{DriverId}] 处理 OPC UA 通知失败: {Name}", DriverId, item.DisplayName);
        }
    }

    /// <summary>
    /// 将 OPC UA StatusCode 映射为 DataQuality
    /// </summary>
    private static DataQuality MapStatusCode(StatusCode statusCode)
    {
        if (StatusCode.IsGood(statusCode)) return DataQuality.Good;
        if (StatusCode.IsUncertain(statusCode)) return DataQuality.Uncertain;
        return DataQuality.Bad;
    }
}

/// <summary>
/// OPC UA 驱动内部配置
/// </summary>
internal sealed class OpcUaDriverConfig
{
    public string Endpoint { get; set; } = "opc.tcp://localhost:4840";
    public string SecurityPolicy { get; set; } = "None";
    public int PublishingIntervalMs { get; set; } = 1000;
    public int SamplingIntervalMs { get; set; } = 500;
    public List<NodeDefinition> Nodes { get; set; } = [];
}

/// <summary>
/// OPC UA 节点定义
/// </summary>
internal sealed class NodeDefinition
{
    public string NodeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
