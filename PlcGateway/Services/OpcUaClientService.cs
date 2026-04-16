using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using PlcGateway.Configuration;

namespace PlcGateway.Services;

/// <summary>
/// OPC UA 客户端服务 - 订阅 OPC UA Server 节点
/// </summary>
public class OpcUaClientService : IDisposable
{
    private readonly OpcUaSettings _settings;
    private readonly MqttPublishService _mqttPublish;
    private readonly ILogger<OpcUaClientService> _logger;
    private ApplicationInstance? _application;
    private Session? _session;
    private Subscription? _subscription;

    public OpcUaClientService(
        OpcUaSettings settings,
        MqttPublishService mqttPublish,
        ILogger<OpcUaClientService> logger)
    {
        _settings = settings;
        _mqttPublish = mqttPublish;
        _logger = logger;
    }

    /// <summary>
    /// 启动 OPC UA 客户端
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("[OPC UA] 服务已禁用");
            return;
        }

        try
        {
            await ConnectAsync(cancellationToken);
            CreateSubscription();
            _logger.LogInformation("[OPC UA] 已连接到 {Endpoint}", _settings.Endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OPC UA] 启动失败");
            throw;
        }
    }

    /// <summary>
    /// 停止 OPC UA 客户端
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_subscription != null)
        {
            _subscription.Delete(true);
            _subscription = null;
        }

        if (_session != null)
        {
            _session.Close();
            _session.Dispose();
            _session = null;
        }

        _logger.LogInformation("[OPC UA] 已断开连接");
        await Task.CompletedTask;
    }

    /// <summary>
    /// 连接到 OPC UA Server
    /// </summary>
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        // 创建应用配置
        var config = new ApplicationConfiguration
        {
            ApplicationName = "PlcGateway",
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
        using var discoveryClient = DiscoveryClient.Create(new Uri(_settings.Endpoint));
        var endpoints = await discoveryClient.GetEndpointsAsync(null, cancellationToken);
        
        var endpointDescription = endpoints.FirstOrDefault(e => e.SecurityPolicyUri == SecurityPolicies.None)
            ?? endpoints.First();
        
        var endpointConfiguration = EndpointConfiguration.Create(config);
        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

        _session = await Session.Create(
            config,
            endpoint,
            false,
            "PlcGateway",
            60000,
            new UserIdentity(new AnonymousIdentityToken()),
            null);
    }

    /// <summary>
    /// 创建订阅
    /// </summary>
    private void CreateSubscription()
    {
        if (_session == null) return;

        _subscription = new Subscription(_session.DefaultSubscription)
        {
            PublishingInterval = 1000,
            DisplayName = "PlcGatewaySubscription"
        };

        foreach (var node in _settings.Nodes)
        {
            var monitoredItem = new MonitoredItem(_subscription.DefaultItem)
            {
                StartNodeId = node.NodeId,
                AttributeId = Attributes.Value,
                DisplayName = node.Name,
                SamplingInterval = 500,
                QueueSize = 10
            };

            monitoredItem.Notification += async (item, e) =>
            {
                if (e.NotificationValue is MonitoredItemNotification notification && notification.Value != null)
                {
                    _logger.LogInformation("[OPC UA] 收到 {Name} = {Value}", node.Name, notification.Value.Value);
                    await _mqttPublish.PublishAsync("opcua", node.Name.ToLowerInvariant(), notification.Value.Value, CancellationToken.None);
                }
                else if (e.NotificationValue is DataValue dataValue && dataValue.Value != null)
                {
                    _logger.LogInformation("[OPC UA] 收到 {Name} = {Value}", node.Name, dataValue.Value);
                    await _mqttPublish.PublishAsync("opcua", node.Name.ToLowerInvariant(), dataValue.Value, CancellationToken.None);
                }
            };

            _subscription.AddItem(monitoredItem);
        }

        _session.AddSubscription(_subscription);
        _subscription.Create();
        _subscription.ApplyChanges();
        _logger.LogInformation("[OPC UA] 已创建订阅，监控 {Count} 个节点, PublishingInterval: {Interval}ms", 
            _settings.Nodes.Count, _subscription.PublishingInterval);
        
        foreach (var item in _subscription.MonitoredItems)
        {
            _logger.LogInformation("[OPC UA] 监控项: {Name}, NodeId: {NodeId}, SamplingInterval: {Sampling}", 
                item.DisplayName, item.StartNodeId, item.SamplingInterval);
        }
    }

    public void Dispose()
    {
        _subscription?.Delete(true);
        _session?.Close();
        _session?.Dispose();
        GC.SuppressFinalize(this);
    }
}
