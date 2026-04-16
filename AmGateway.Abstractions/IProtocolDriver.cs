namespace AmGateway.Abstractions;

/// <summary>
/// 协议驱动核心接口 - 所有协议插件必须实现此接口
/// </summary>
public interface IProtocolDriver : IAsyncDisposable
{
    /// <summary>
    /// 驱动实例唯一标识
    /// </summary>
    string DriverId { get; }

    /// <summary>
    /// 初始化驱动，注入上下文（配置、日志、数据输出管道）
    /// </summary>
    Task InitializeAsync(DriverContext context, CancellationToken ct = default);

    /// <summary>
    /// 启动数据采集
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 停止数据采集（优雅关闭）
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}
