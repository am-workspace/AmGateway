namespace AmGateway.Abstractions.Metadata;

/// <summary>
/// 发布器元数据标记 - 与 DriverMetadataAttribute 对称设计
/// 标记在 IPublisher 实现类上，PluginManager 通过反射读取
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PublisherMetadataAttribute : Attribute
{
    /// <summary>
    /// 显示名称，如 "MQTT"
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 传输标识符，如 "mqtt"，用于与配置中的 Transport 字段匹配
    /// </summary>
    public required string TransportName { get; init; }

    /// <summary>
    /// 发布器版本
    /// </summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// 描述
    /// </summary>
    public string Description { get; init; } = "";
}
