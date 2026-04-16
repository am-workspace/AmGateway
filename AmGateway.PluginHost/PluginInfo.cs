using AmGateway.Abstractions.Metadata;

namespace AmGateway.PluginHost;

/// <summary>
/// 已加载插件的描述信息
/// 一个插件 DLL 可以同时包含 Driver 和 Publisher 实现
/// </summary>
public sealed class PluginInfo
{
    /// <summary>
    /// 插件目录路径
    /// </summary>
    public required string PluginDirectory { get; init; }

    /// <summary>
    /// 插件的 AssemblyLoadContext
    /// </summary>
    internal PluginLoadContext LoadContext { get; init; } = null!;

    /// <summary>
    /// 实现 IProtocolDriver 的类型（可选）
    /// </summary>
    public Type? DriverType { get; init; }

    /// <summary>
    /// 驱动元数据（可选，与 DriverType 配对）
    /// </summary>
    public DriverMetadataAttribute? DriverMetadata { get; init; }

    /// <summary>
    /// 实现 IPublisher 的类型（可选）
    /// </summary>
    public Type? PublisherType { get; init; }

    /// <summary>
    /// 发布器元数据（可选，与 PublisherType 配对）
    /// </summary>
    public PublisherMetadataAttribute? PublisherMetadata { get; init; }
}
