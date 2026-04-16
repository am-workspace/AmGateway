using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AmGateway.Abstractions;

/// <summary>
/// 发布器上下文 - 在 InitializeAsync 阶段由主机注入
/// 与 DriverContext 对称设计
/// </summary>
public sealed class PublisherContext
{
    /// <summary>
    /// 该发布器实例的配置节（从 appsettings.json 的 Settings 子节提取）
    /// </summary>
    public required IConfiguration Configuration { get; init; }

    /// <summary>
    /// 日志工厂，发布器可通过它创建具名 Logger
    /// </summary>
    public required ILoggerFactory LoggerFactory { get; init; }

    /// <summary>
    /// 发布器实例唯一标识
    /// </summary>
    public required string PublisherInstanceId { get; init; }
}
