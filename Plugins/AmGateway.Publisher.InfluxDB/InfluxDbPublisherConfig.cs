namespace AmGateway.Publisher.InfluxDB;

/// <summary>
/// InfluxDB 发布器内部配置
/// </summary>
internal sealed class InfluxDbPublisherConfig
{
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = "http://localhost:8086";
    public string Token { get; set; } = "";
    public string Org { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Measurement { get; set; } = "datapoints";
    public int BatchSize { get; set; } = 100;
    public int FlushIntervalMs { get; set; } = 1000;
    public int ReconnectDelayMs { get; set; } = 5000;
    public bool GzipEncoding { get; set; } = false;
}
