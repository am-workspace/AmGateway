namespace AmGateway.Abstractions.Models;

/// <summary>
/// 数据转换规则
/// </summary>
public sealed class TransformRule
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
    /// 匹配的数据点 Tag 模式（支持 glob: "opcua-01/*", "modbus-01/Temperature"）
    /// </summary>
    public required string TagPattern { get; init; }

    /// <summary>
    /// 转换类型
    /// </summary>
    public required TransformType Type { get; init; }

    /// <summary>
    /// 转换参数（JSON，不同类型有不同结构）
    /// </summary>
    public required string ParametersJson { get; init; }

    /// <summary>
    /// 优先级（数值越小越先执行）
    /// </summary>
    public int Priority { get; init; } = 100;
}

/// <summary>
/// 转换类型
/// </summary>
public enum TransformType
{
    /// <summary>线性缩放 y = kx + b</summary>
    Linear,
    /// <summary>分段线性（查表插值）</summary>
    PiecewiseLinear,
    /// <summary>单位换算（预定义换算公式）</summary>
    UnitConversion,
    /// <summary>死区过滤（值变化小于阈值时丢弃）</summary>
    Deadband,
    /// <summary>JavaScript 表达式（自定义逻辑）</summary>
    Script
}
