using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AmGateway.Abstractions;

/// <summary>
/// 驱动上下文 - 在 InitializeAsync 阶段由主机注入给驱动
/// 因为插件通过 Activator.CreateInstance 实例化，不能使用构造函数 DI
/// </summary>
public sealed class DriverContext
{
    /// <summary>
    /// 该驱动实例的配置节（从 appsettings.json 的 Settings 子节提取）
    /// </summary>
    public required IConfiguration Configuration { get; init; }

    /// <summary>
    /// 日志工厂，驱动可通过它创建具名 Logger
    /// </summary>
    public required ILoggerFactory LoggerFactory { get; init; }

    /// <summary>
    /// 数据输出管道，驱动通过它推送 DataPoint
    /// </summary>
    public required IDataSink DataSink { get; init; }

    /// <summary>
    /// 驱动实例唯一标识
    /// </summary>
    public required string DriverInstanceId { get; init; }
}
