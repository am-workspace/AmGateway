using AmGateway.Abstractions.Models;
using AmGateway.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace AmGateway.Services;

/// <summary>
/// REST API 端点映射扩展 — 所有端点默认需要 JWT 认证
/// </summary>
public static class ApiEndpoints
{
    public static WebApplication MapGatewayApi(this WebApplication app)
    {
        var jwtSettings = app.Services.GetRequiredService<IOptions<JwtAuthSettings>>().Value;
        var requireAuth = jwtSettings.Enabled;

        var api = app.MapGroup("/api");

        if (requireAuth)
        {
            api.RequireAuthorization();
        }

        // === 驱动管理 ===
        api.MapGet("/drivers", (GatewayRuntime runtime) =>
        {
            return Results.Ok(runtime.GetDriverInfos());
        });

        api.MapGet("/drivers/{instanceId}", (string instanceId, GatewayRuntime runtime) =>
        {
            var info = runtime.GetDriverInfo(instanceId);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        api.MapPost("/drivers", async (CreateDriverRequest request, GatewayRuntime runtime, CancellationToken ct) =>
        {
            try
            {
                // 将 Settings 字典序列化为 JSON，再通过 BuildConfigurationFromJson 递归展平
                var settingsJson = System.Text.Json.JsonSerializer.Serialize(request.Settings);
                var config = GatewayRuntime.BuildConfigurationFromJsonPublic(settingsJson);

                var info = await runtime.AddDriverAsync(request.Protocol, request.InstanceId, config, ct);
                return Results.Created($"/api/drivers/{info.InstanceId}", info);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        api.MapDelete("/drivers/{instanceId}", async (string instanceId, GatewayRuntime runtime, CancellationToken ct) =>
        {
            try
            {
                await runtime.RemoveDriverAsync(instanceId, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // === 发布器管理 ===
        api.MapGet("/publishers", (GatewayRuntime runtime) =>
        {
            return Results.Ok(runtime.GetPublisherInfos());
        });

        api.MapGet("/publishers/{instanceId}", (string instanceId, GatewayRuntime runtime) =>
        {
            var info = runtime.GetPublisherInfo(instanceId);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        api.MapPost("/publishers", async (CreatePublisherRequest request, GatewayRuntime runtime, CancellationToken ct) =>
        {
            try
            {
                var settingsJson = System.Text.Json.JsonSerializer.Serialize(request.Settings);
                var config = GatewayRuntime.BuildConfigurationFromJsonPublic(settingsJson);

                var info = await runtime.AddPublisherAsync(request.Transport, request.InstanceId, config, ct);
                return Results.Created($"/api/publishers/{info.InstanceId}", info);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        api.MapDelete("/publishers/{instanceId}", async (string instanceId, GatewayRuntime runtime, CancellationToken ct) =>
        {
            try
            {
                await runtime.RemovePublisherAsync(instanceId, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // === 插件信息 ===
        api.MapGet("/plugins", (GatewayRuntime runtime) =>
        {
            var (drivers, publishers) = runtime.GetLoadedPlugins();

            return Results.Ok(new
            {
                drivers = drivers.Select(kv => new
                {
                    protocol = kv.Key,
                    name = kv.Value.DriverMetadata?.Name,
                    version = kv.Value.DriverMetadata?.Version,
                    description = kv.Value.DriverMetadata?.Description
                }),
                publishers = publishers.Select(kv => new
                {
                    transport = kv.Key,
                    name = kv.Value.PublisherMetadata?.Name,
                    version = kv.Value.PublisherMetadata?.Version,
                    description = kv.Value.PublisherMetadata?.Description
                })
            });
        });

        // === 健康检查（免认证，含指标摘要） ===
        api.MapGet("/health", (GatewayRuntime runtime, GatewayMetrics metrics, IDataPipeline pipeline) =>
        {
            var snapshot = metrics.GetSnapshot();
            snapshot["drivers"] = runtime.ActiveDriverCount;
            snapshot["publishers"] = runtime.ActivePublisherCount;
            snapshot["pipelinePending"] = pipeline.PendingCount;
            snapshot["status"] = "healthy";
            snapshot["timestamp"] = DateTimeOffset.UtcNow;
            return Results.Ok(snapshot);
        }).AllowAnonymous();

        // === Prometheus metrics 端点（免认证，标准 /metrics 路径） ===
        app.MapGet("/metrics", (GatewayMetrics metrics) =>
        {
            var text = metrics.ExportPrometheus();
            return Results.Text(text, "text/plain; version=0.0.4; charset=utf-8");
        }).AllowAnonymous();

        // === 配置导出/导入 ===
        api.MapGet("/config/export", async (GatewayRuntime runtime) =>
        {
            var json = await runtime.ConfigRepo.ExportToJsonAsync();
            return Results.Text(json, "application/json");
        });

        api.MapPost("/config/import", async (HttpContext context, GatewayRuntime runtime) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync();
            await runtime.ConfigRepo.ImportFromJsonAsync(json);
            return Results.Ok(new { message = "导入成功，需重启生效" });
        });

        // === 驱动重启 ===
        api.MapPost("/drivers/{instanceId}/restart", async (string instanceId, GatewayRuntime runtime, CancellationToken ct) =>
        {
            try
            {
                await runtime.RestartDriverAsync(instanceId, ct);
                return Results.Ok(new { message = $"驱动 {instanceId} 已重启" });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // === 转换规则管理 ===
        api.MapGet("/transforms", (GatewayRuntime runtime) =>
        {
            return Results.Ok(runtime.GetTransformRules());
        });

        api.MapPost("/transforms", async (CreateTransformRuleRequest request, GatewayRuntime runtime, CancellationToken ct) =>
        {
            try
            {
                var rule = new TransformRule
                {
                    RuleId = request.RuleId,
                    Enabled = request.Enabled,
                    TagPattern = request.TagPattern,
                    Type = request.Type,
                    ParametersJson = request.ParametersJson,
                    Priority = request.Priority
                };
                var created = await runtime.AddTransformRuleAsync(rule, ct);
                return Results.Created($"/api/transforms/{created.RuleId}", created);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        api.MapDelete("/transforms/{ruleId}", async (string ruleId, GatewayRuntime runtime, CancellationToken ct) =>
        {
            try
            {
                await runtime.RemoveTransformRuleAsync(ruleId, ct);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // === 路由规则管理 ===
        api.MapGet("/routes", (GatewayRuntime runtime) =>
        {
            return Results.Ok(runtime.GetRouteRules());
        });

        api.MapPost("/routes", async (CreateRouteRuleRequest request, GatewayRuntime runtime, CancellationToken ct) =>
        {
            try
            {
                var rule = new RouteRule
                {
                    RuleId = request.RuleId,
                    Enabled = request.Enabled,
                    TagPattern = request.TagPattern,
                    TargetPublisherIds = request.TargetPublisherIds,
                    Priority = request.Priority
                };
                var created = await runtime.AddRouteRuleAsync(rule, ct);
                return Results.Created($"/api/routes/{created.RuleId}", created);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        api.MapDelete("/routes/{ruleId}", async (string ruleId, GatewayRuntime runtime, CancellationToken ct) =>
        {
            try
            {
                await runtime.RemoveRouteRuleAsync(ruleId, ct);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // 优雅关机端点
        api.MapPost("/shutdown", async (IHostApplicationLifetime lifetime, CancellationToken ct) =>
        {
            lifetime.StopApplication();
            return Results.Ok(new { message = "网关正在优雅关闭..." });
        });

        return app;
    }
}

/// <summary>创建驱动请求</summary>
public sealed class CreateDriverRequest
{
    public required string Protocol { get; init; }
    public required string InstanceId { get; init; }
    public Dictionary<string, object?>? Settings { get; init; }
}

/// <summary>创建发布器请求</summary>
public sealed class CreatePublisherRequest
{
    public required string Transport { get; init; }
    public required string InstanceId { get; init; }
    public Dictionary<string, object?>? Settings { get; init; }
}

/// <summary>创建转换规则请求</summary>
public sealed class CreateTransformRuleRequest
{
    public required string RuleId { get; init; }
    public bool Enabled { get; init; } = true;
    public required string TagPattern { get; init; }
    public required TransformType Type { get; init; }
    public required string ParametersJson { get; init; }
    public int Priority { get; init; } = 100;
}

/// <summary>创建路由规则请求</summary>
public sealed class CreateRouteRuleRequest
{
    public required string RuleId { get; init; }
    public bool Enabled { get; init; } = true;
    public required string TagPattern { get; init; }
    public required List<string> TargetPublisherIds { get; init; } = [];
    public int Priority { get; init; } = 100;
}
