namespace AmGateway.Publisher.Mqtt;

/// <summary>
/// MQTT 发布器内部配置
/// </summary>
internal sealed class MqttPublisherConfig
{
    public bool Enabled { get; set; } = true;
    public string Broker { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string TopicPrefix { get; set; } = "amgateway";
    public string ClientId { get; set; } = "AmGateway";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int ReconnectDelayMs { get; set; } = 5000;
    public bool UseTls { get; set; } = false;
}
