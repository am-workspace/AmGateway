namespace AmGateway.Abstractions.Models;

/// <summary>
/// 统一数据点模型 - 所有协议驱动输出的标准化数据格式
/// record struct: 值类型，低 GC 压力，适合高吞吐场景
/// </summary>
public readonly record struct DataPoint
{
    /// <summary>
    /// 数据点标签，格式建议 "{driverId}/{name}"
    /// </summary>
    public required string Tag { get; init; }

    /// <summary>
    /// 采集值（ushort, double, bool, string 等）
    /// </summary>
    public required object? Value { get; init; }

    /// <summary>
    /// 采集时间戳（UTC）
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// 数据质量
    /// </summary>
    public required DataQuality Quality { get; init; }

    /// <summary>
    /// 产生该数据的驱动实例 ID
    /// </summary>
    public required string SourceDriver { get; init; }

    /// <summary>
    /// 可选扩展信息（单位、数据类型、地址等）
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
