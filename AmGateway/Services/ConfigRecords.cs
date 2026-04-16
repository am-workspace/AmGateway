using AmGateway.Abstractions.Models;

namespace AmGateway.Services;

/// <summary>
/// 持久化的驱动配置记录
/// </summary>
public sealed class DriverConfigRecord
{
    public required string InstanceId { get; init; }
    public required string Protocol { get; init; }
    public bool Enabled { get; init; } = true;
    public required string SettingsJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 持久化的发布器配置记录
/// </summary>
public sealed class PublisherConfigRecord
{
    public required string InstanceId { get; init; }
    public required string Transport { get; init; }
    public bool Enabled { get; init; } = true;
    public required string SettingsJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 持久化的转换规则记录
/// </summary>
public sealed class TransformRuleRecord
{
    public required string RuleId { get; init; }
    public bool Enabled { get; init; } = true;
    public required string TagPattern { get; init; }
    public required TransformType Type { get; init; }
    public required string ParametersJson { get; init; }
    public int Priority { get; init; } = 100;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 持久化的路由规则记录
/// </summary>
public sealed class RouteRuleRecord
{
    public required string RuleId { get; init; }
    public bool Enabled { get; init; } = true;
    public required string TagPattern { get; init; }
    public required string TargetPublisherIdsJson { get; init; }
    public int Priority { get; init; } = 100;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
