using AmGateway.Abstractions;
using AmGateway.Abstractions.Models;

namespace AmGateway.Pipeline;

/// <summary>
/// 数据路由解析器接口
/// 根据路由规则决定数据点应该发送到哪些发布器
/// </summary>
public interface IRouteResolver
{
    /// <summary>
    /// 解析数据点的目标发布器列表
    /// </summary>
    /// <param name="point">数据点</param>
    /// <param name="allPublishers">所有可用的发布器</param>
    /// <returns>目标发布器列表</returns>
    IReadOnlyList<IPublisher> Resolve(DataPoint point, IReadOnlyList<IPublisher> allPublishers);

    /// <summary>
    /// 加载路由规则
    /// </summary>
    void LoadRules(IReadOnlyList<RouteRule> rules);

    /// <summary>
    /// 获取当前已加载的规则
    /// </summary>
    IReadOnlyList<RouteRule> GetRules();
}
