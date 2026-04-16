using AmGateway.Abstractions;

namespace AmGateway.Pipeline;

/// <summary>
/// 数据管道接口
/// </summary>
public interface IDataPipeline
{
    /// <summary>
    /// 写入端（给驱动使用的 IDataSink 实现）
    /// </summary>
    IDataSink Writer { get; }

    /// <summary>
    /// 设置北向发布器列表（由 GatewayHostService 在启动时注入）
    /// </summary>
    void SetPublishers(IReadOnlyList<IPublisher> publishers);

    /// <summary>
    /// 动态添加发布器（运行时热插拔）
    /// </summary>
    void AddPublisher(IPublisher publisher);

    /// <summary>
    /// 动态移除发布器（运行时热插拔）
    /// </summary>
    bool RemovePublisher(IPublisher publisher);

    /// <summary>
    /// 设置转换引擎
    /// </summary>
    void SetTransformEngine(ITransformEngine engine);

    /// <summary>
    /// 设置路由解析器
    /// </summary>
    void SetRouteResolver(IRouteResolver resolver);

    /// <summary>
    /// 获取 Channel 中待处理的数据点数量
    /// </summary>
    int PendingCount { get; }

    /// <summary>
    /// 启动消费循环
    /// </summary>
    Task StartConsumingAsync(CancellationToken ct);

    /// <summary>
    /// 停止消费（先完成 Channel 排空再退出）
    /// </summary>
    Task StopAsync();
}
