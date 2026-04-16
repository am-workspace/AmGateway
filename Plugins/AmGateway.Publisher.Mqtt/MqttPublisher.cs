using AmGateway.Abstractions;
using AmGateway.Abstractions.Metadata;
using AmGateway.Abstractions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using System.Text.Json;

namespace AmGateway.Publisher.Mqtt;

/// <summary>
/// MQTT 北向发布器 - 将 DataPoint 发布到外部 MQTT Broker
/// 实现方式与老项目 PlcGateway/Services/MqttPublishService.cs 一致
/// </summary>
[PublisherMetadata(
    Name = "MQTT",
    TransportName = "mqtt",
    Version = "1.0.0",
    Description = "MQTT 北向发布器，将数据发布到外部 MQTT Broker")]
public sealed class MqttPublisher : IPublisher
{
    private ILogger<MqttPublisher> _logger = null!;
    private MqttPublisherConfig _config = null!;
    private IMqttClient? _mqttClient;
    private MqttClientOptions? _mqttOptions;
    private bool _reconnecting;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    public string PublisherId { get; private set; } = string.Empty;

    public Task InitializeAsync(PublisherContext context, CancellationToken ct = default)
    {
        PublisherId = context.PublisherInstanceId;
        _logger = context.LoggerFactory.CreateLogger<MqttPublisher>();

        _config = new MqttPublisherConfig();
        context.Configuration.Bind(_config);

        _logger.LogInformation("[{PublisherId}] MQTT 发布器已初始化 - Broker: {Broker}:{Port}, Topic: {TopicPrefix}/#",
            PublisherId, _config.Broker, _config.Port, _config.TopicPrefix);

        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var factory = new MqttClientFactory();
        _mqttClient = factory.CreateMqttClient();

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_config.Broker, _config.Port)
            .WithClientId(_config.ClientId)
            .WithCleanSession();

        if (!string.IsNullOrEmpty(_config.Username))
        {
            optionsBuilder.WithCredentials(_config.Username, _config.Password ?? "");
            _logger.LogInformation("[{PublisherId}] MQTT 使用用户名密码认证: {Username}", PublisherId, _config.Username);
        }

        _mqttOptions = optionsBuilder.Build();

        // 注册断线重连
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;

        await _mqttClient.ConnectAsync(_mqttOptions, ct);
        _logger.LogInformation("[{PublisherId}] MQTT 已连接到 {Broker}:{Port}",
            PublisherId, _config.Broker, _config.Port);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_mqttClient != null)
        {
            _mqttClient.DisconnectedAsync -= OnDisconnectedAsync;

            if (_mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync(cancellationToken: ct);
            }
        }

        _logger.LogInformation("[{PublisherId}] MQTT 发布器已停止", PublisherId);
    }

    public async ValueTask PublishAsync(DataPoint point, CancellationToken ct = default)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            // 断线时静默丢弃，不阻塞采集
            return;
        }

        try
        {
            var topic = BuildTopic(point);
            var payload = BuildPayload(point);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.PublishAsync(message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{PublisherId}] MQTT 发布失败: {Tag}", PublisherId, point.Tag);
        }
    }

    public async ValueTask PublishBatchAsync(IReadOnlyList<DataPoint> points, CancellationToken ct = default)
    {
        foreach (var point in points)
        {
            await PublishAsync(point, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _mqttClient?.Dispose();
        _reconnectLock.Dispose();
    }

    /// <summary>
    /// 构建 MQTT Topic: {TopicPrefix}/{SourceDriver}/{Tag最后一部分}
    /// </summary>
    private string BuildTopic(DataPoint point)
    {
        // Tag 格式为 "{driverId}/{name}"，取 name 部分作为最后一段
        var tagSuffix = point.Tag.Contains('/')
            ? point.Tag[(point.Tag.IndexOf('/') + 1)..]
            : point.Tag;

        return $"{_config.TopicPrefix}/{point.SourceDriver}/{tagSuffix}".ToLowerInvariant();
    }

    /// <summary>
    /// 构建 JSON Payload
    /// </summary>
    private static string BuildPayload(DataPoint point)
    {
        var payload = new
        {
            tag = point.Tag,
            value = point.Value,
            timestamp = point.Timestamp.ToString("O"),
            quality = point.Quality.ToString(),
            sourceDriver = point.SourceDriver,
            metadata = point.Metadata
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// 断线自动重连
    /// </summary>
    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (_reconnecting || _mqttOptions == null)
            return;

        await _reconnectLock.WaitAsync();
        try
        {
            if (_reconnecting)
                return;

            _reconnecting = true;
            _logger.LogWarning("[{PublisherId}] MQTT 连接断开，开始自动重连...", PublisherId);

            for (var i = 0; ; i++)
            {
                try
                {
                    await Task.Delay(_config.ReconnectDelayMs);
                    await _mqttClient!.ConnectAsync(_mqttOptions);
                    _logger.LogInformation("[{PublisherId}] MQTT 重连成功 (第 {Attempt} 次)", PublisherId, i + 1);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{PublisherId}] MQTT 重连失败 (第 {Attempt} 次)，继续尝试...", PublisherId, i + 1);
                }
            }
        }
        finally
        {
            _reconnecting = false;
            _reconnectLock.Release();
        }
    }
}
