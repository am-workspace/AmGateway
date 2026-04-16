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

    public RouteResolver(ILogger<RouteResolver> logger)
    {
        _logger = logger;
    }

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

    public void LoadRules(IReadOnlyList<RouteRule> rules)
    {
        lock (_lock)
        {
            _rules = rules.OrderBy(r => r.Priority).ToList();
        }
        _logger.LogInformation("[RouteResolver] 已加载 {Count} 条路由规则", rules.Count);
    }

    public IReadOnlyList<RouteRule> GetRules()
    {
        lock (_lock)
        {
            return _rules.ToList();
        }
    }

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
