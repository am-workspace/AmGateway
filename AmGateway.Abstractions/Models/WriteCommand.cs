namespace AmGateway.Abstractions.Models;

/// <summary>
/// 写命令模型（预定义，Phase 1 不使用）
/// </summary>
public readonly record struct WriteCommand
{
    /// <summary>
    /// 目标标签
    /// </summary>
    public required string Tag { get; init; }

    /// <summary>
    /// 写入值
    /// </summary>
    public required object Value { get; init; }

    /// <summary>
    /// 可选扩展信息
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
