namespace AmGateway.Abstractions.Configuration;

/// <summary>
/// 通用驱动配置基类 - 所有驱动配置共享的字段
/// </summary>
public class DriverConfiguration
{
    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 实例标识（可选，不设则自动生成）
    /// </summary>
    public string? InstanceId { get; set; }
}
