namespace AmGateway.Services;

/// <summary>
/// JWT 认证配置 — 从 appsettings.json 的 Authentication:Jwt 节读取
/// </summary>
public sealed class JwtAuthSettings
{
    /// <summary>密钥（至少 16 字符）</summary>
    public required string SecretKey { get; init; }

    /// <summary>签发者</summary>
    public string Issuer { get; init; } = "AmGateway";

    /// <summary>受众</summary>
    public string Audience { get; init; } = "AmGateway";

    /// <summary>Token 有效时间（小时）</summary>
    public int ExpireHours { get; init; } = 24;

    /// <summary>是否启用认证（设为 false 可关闭）</summary>
    public bool Enabled { get; init; } = true;
}
