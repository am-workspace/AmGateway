namespace AmGateway.Abstractions.Models;

/// <summary>
/// 数据质量枚举
/// </summary>
[Flags]
public enum DataQuality : byte
{
    Good         = 0,
    Uncertain    = 1,
    Bad          = 2,
    ConfigError  = 4,
    NotConnected = 8
}
