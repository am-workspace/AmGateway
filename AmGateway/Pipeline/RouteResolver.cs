using System.Text.RegularExpressions;
using AmGateway.Abstractions;
using AmGateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AmGateway.Pipeline;

/// <summary>
/// 数据路由解析器实现
/// 根据路由规则将数据点分发到指定的发布器
/// 无匹配规则时路由到所有发布器（默认行为）
/// </summary>
public sealed class RouteResolver : IRouteResolver
{
    private readonly ILogger<RouteResolver> _logger;
    private List<RouteRule> _rules = [];
    private readonly object _lock = new();

    /// <summary>
    /// 初始化路由解析器
    /// </summary>
    /// <param name="logger">日志记录器</param>
    public RouteResolver(ILogger<RouteResolver> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 根据路由规则解析数据点应发送到的目标发布器列表
    /// </summary>
    /// <param name="point">待路由的数据点</param>
    /// <param name="allPublishers">系统中所有已注册的发布器</param>
    /// <returns>匹配到的目标发布器列表；无匹配规则时返回所有发布器</returns>
    public IReadOnlyList<IPublisher> Resolve(DataPoint point, IReadOnlyList<IPublisher> allPublishers)
    {
        List<RouteRule> currentRules;
        lock (_lock)
        {
            currentRules = _rules;
        }

        // 查找第一个匹配的规则（按优先级排序）
        var matchedRule = currentRules
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority)
            .FirstOrDefault(r => IsTagMatch(point.Tag, r.TagPattern));

        if (matchedRule == null)
        {
            // 无匹配规则，路由到所有发布器
            return allPublishers;
        }

        if (matchedRule.TargetPublisherIds.Count == 0)
        {
            // 目标为空列表 = 路由到所有发布器
            return allPublishers;
        }

        // 按发布器 ID 筛选
        var targetIds = new HashSet<string>(matchedRule.TargetPublisherIds, StringComparer.OrdinalIgnoreCase);
        return allPublishers.Where(p => targetIds.Contains(p.PublisherId)).ToList();
    }

    /// <summary>
    /// 加载路由规则列表，并按优先级排序后缓存
    /// </summary>
    /// <param name="rules">路由规则集合</param>
    public void LoadRules(IReadOnlyList<RouteRule> rules)
    {
        lock (_lock)
        {
            _rules = rules.OrderBy(r => r.Priority).ToList();
        }
        _logger.LogInformation("[RouteResolver] 已加载 {Count} 条路由规则", rules.Count);
    }

    /// <summary>
    /// 获取当前已加载的路由规则列表
    /// </summary>
    /// <returns>当前缓存的路由规则只读集合</returns>
    public IReadOnlyList<RouteRule> GetRules()
    {
        lock (_lock)
        {
            return _rules.ToList();
        }
    }

    /// <summary>
    /// 判断标签名称是否匹配给定的通配符模式（支持 *、? 和 **）
    /// </summary>
    /// <param name="tag">数据点标签名称</param>
    /// <param name="pattern">匹配模式，如 "Sensor_*" 或 "**"</param>
    /// <returns>匹配成功返回 true，否则返回 false</returns>
    private static bool IsTagMatch(string tag, string pattern)
    {
        if (pattern == "*" || pattern == "**")
            return true;

        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(tag, pattern, StringComparison.OrdinalIgnoreCase);

        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(tag, regexPattern, RegexOptions.IgnoreCase);
    }
}
