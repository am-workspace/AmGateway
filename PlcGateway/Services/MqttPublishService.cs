using MQTTnet;
using MQTTnet.Protocol;
using PlcGateway.Configuration;
using System.Text.Json;

namespace PlcGateway.Services;

/// <summary>
/// MQTT 发布服务 - 将采集的数据发布到 MQTT Broker
/// </summary>
public class MqttPublishService : IDisposable
{
    private readonly MqttSettings _settings;
    private readonly ILogger<MqttPublishService> _logger;
    private IMqttClient? _mqttClient;
    private MqttClientOptions? _mqttOptions;

    public MqttPublishService(MqttSettings settings, ILogger<MqttPublishService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 连接到 MQTT Broker
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("[MQTT] 服务已禁用");
            return;
        }

        try
        {
            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(_settings.Broker, _settings.Port)
                .WithClientId(_settings.ClientId)
                .WithCleanSession();

            // 条件式认证
            if (!string.IsNullOrEmpty(_settings.Username))
            {
                optionsBuilder.WithCredentials(_settings.Username, _settings.Password ?? "");
                _logger.LogInformation("[MQTT] 使用用户名密码认证: {Username}", _settings.Username);
            }

            _mqttOptions = optionsBuilder.Build();

            await _mqttClient.ConnectAsync(_mqttOptions, cancellationToken);
            _logger.LogInformation("[MQTT] 已连接到 {Broker}:{Port}", _settings.Broker, _settings.Port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MQTT] 连接失败");
            throw;
        }
    }

    /// <summary>
    /// 发布消息到指定 Topic
    /// </summary>
    public async Task PublishAsync(string source, string parameter, object value, CancellationToken cancellationToken = default)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            _logger.LogWarning("[MQTT] 未连接，无法发布消息");
            return;
        }

        var topic = $"{_settings.TopicPrefix}/{source}/{parameter}".ToLowerInvariant();
        var payload = new
        {
            parameter,
            value,
            timestamp = DateTime.UtcNow.ToString("O"),
            source
        };

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(payload))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _mqttClient.PublishAsync(message, cancellationToken);
        _logger.LogDebug("[MQTT] 发布到 {Topic}: {Payload}", topic, payload.value);
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_mqttClient?.IsConnected == true)
        {
            await _mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
            _logger.LogInformation("[MQTT] 已断开连接");
        }
    }

    public void Dispose()
    {
        _mqttClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
