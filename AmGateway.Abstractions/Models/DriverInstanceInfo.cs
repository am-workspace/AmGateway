namespace AmGateway.Abstractions.Models;

/// <summary>
/// 运行中驱动实例的信息快照
/// </summary>
public sealed class DriverInstanceInfo
{
    public required string InstanceId { get; init; }
    public required string Protocol { get; init; }
    public required RuntimeStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
