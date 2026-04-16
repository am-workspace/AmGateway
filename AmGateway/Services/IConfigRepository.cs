using AmGateway.Abstractions.Models;

namespace AmGateway.Services;

/// <summary>
/// 配置持久化仓库接口
/// </summary>
public interface IConfigRepository : IDisposable
{
    // === 驱动配置 ===
    Task<IReadOnlyList<DriverConfigRecord>> GetAllDriversAsync();
    Task<DriverConfigRecord?> GetDriverAsync(string instanceId);
    Task SaveDriverAsync(DriverConfigRecord record);
    Task DeleteDriverAsync(string instanceId);

    // === 发布器配置 ===
    Task<IReadOnlyList<PublisherConfigRecord>> GetAllPublishersAsync();
    Task<PublisherConfigRecord?> GetPublisherAsync(string instanceId);
    Task SavePublisherAsync(PublisherConfigRecord record);
    Task DeletePublisherAsync(string instanceId);

    // === 转换规则 ===
    Task<IReadOnlyList<TransformRuleRecord>> GetAllTransformRulesAsync();
    Task<TransformRuleRecord?> GetTransformRuleAsync(string ruleId);
    Task SaveTransformRuleAsync(TransformRuleRecord record);
    Task DeleteTransformRuleAsync(string ruleId);

    // === 路由规则 ===
    Task<IReadOnlyList<RouteRuleRecord>> GetAllRouteRulesAsync();
    Task<RouteRuleRecord?> GetRouteRuleAsync(string ruleId);
    Task SaveRouteRuleAsync(RouteRuleRecord record);
    Task DeleteRouteRuleAsync(string ruleId);

    // === JSON 导出 ===
    Task<string> ExportToJsonAsync();
    Task ImportFromJsonAsync(string json);
}
