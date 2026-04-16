namespace AmGateway.Abstractions.Models;

/// <summary>
/// 运行中发布器实例的信息快照
/// </summary>
public sealed class PublisherInstanceInfo
{
    public required string InstanceId { get; init; }
    public required string Transport { get; init; }
    public required RuntimeStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
