namespace AmGateway.Abstractions.Models;

/// <summary>
/// 写结果模型（预定义，Phase 1 不使用）
/// </summary>
public readonly record struct WriteResult
{
    /// <summary>
    /// 是否写入成功
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// 错误信息（失败时）
    /// </summary>
    public string? ErrorMessage { get; init; }
}
