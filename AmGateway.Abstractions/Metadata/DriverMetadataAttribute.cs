namespace AmGateway.Abstractions.Metadata;

/// <summary>
/// 驱动元数据标记 - 标记在 IProtocolDriver 实现类上
/// PluginManager 通过反射读取，无需实例化驱动即可获取元信息
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class DriverMetadataAttribute : Attribute
{
    /// <summary>
    /// 显示名称，如 "Modbus TCP"
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 协议标识符，如 "modbus-tcp"，用于与配置中的 Protocol 字段匹配
    /// </summary>
    public required string ProtocolName { get; init; }

    /// <summary>
    /// 驱动版本
    /// </summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// 描述
    /// </summary>
    public string Description { get; init; } = "";
}
