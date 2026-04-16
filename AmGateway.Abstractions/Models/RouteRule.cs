namespace AmGateway.Abstractions.Models;

/// <summary>
/// 数据路由规则
/// </summary>
public sealed class RouteRule
{
    /// <summary>
    /// 规则唯一标识
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 匹配的数据点 Tag 模式（支持 glob）
    /// </summary>
    public required string TagPattern { get; init; }

    /// <summary>
    /// 目标发布器实例 ID 列表（空=路由到所有发布器）
    /// </summary>
    public required List<string> TargetPublisherIds { get; init; }

    /// <summary>
    /// 优先级（数值越小越先匹配，首次匹配生效）
    /// </summary>
    public int Priority { get; init; } = 100;
}
