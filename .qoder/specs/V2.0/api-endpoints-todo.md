# ApiEndpoints 审查待办

> 审查文件：`AmGateway/Services/ApiEndpoints.cs`
> 审查日期：2026-05-05

---

## 高优先级

### 1. `/api/shutdown` 端点无任何保护——任何人可关机

**现状**：任何通过认证的用户都能远程调用 `POST /api/shutdown` 关闭网关。

```csharp
api.MapPost("/shutdown", async (IHostApplicationLifetime lifetime, CancellationToken ct) =>
{
    lifetime.StopApplication();
    return Results.Ok(new { message = "网关正在优雅关闭..." });
});
```

**问题**：工业网关误触发关机可能导致数据丢失。

**改动点**：
- 增加二次确认机制（如请求体需带 `confirm: true`）
- 或限制为特定角色
- 或直接移除（改用 SIGTERM 信号）

---

### 2. `/api/config/import` 无输入校验——可注入任意配置

**现状**：直接读取请求体为 JSON 字符串，无大小限制、无格式校验。

```csharp
api.MapPost("/config/import", async (HttpContext context, GatewayRuntime runtime) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var json = await reader.ReadToEndAsync();          // 无大小限制
    await runtime.ConfigRepo.ImportFromJsonAsync(json); // 无格式校验
    return Results.Ok(new { message = "导入成功，需重启生效" });
});
```

**问题**：
- 无大小限制：可上传 GB 级 JSON 打爆内存
- 无格式校验：恶意 JSON 可注入任意驱动配置（如连接内网地址）
- 导入后需重启但无实际重启机制，运行时与持久化状态不一致

**改动点**：
- 限制请求体大小（如 `app.UseRequestSizeLimit(1_000_000)`）
- 校验 JSON 格式和内容合法性
- 导入后触发运行时重载，或返回明确的后续操作指引

---

### 3. 健康检查永远返回 `healthy`，不反映真实状态

**现状**：无论驱动状态如何，健康检查始终返回 `status: "healthy"`。

```csharp
snapshot["status"] = "healthy";
```

**问题**：所有驱动 Error、Pipeline 堆积 10000 条数据时仍显示 healthy，监控失灵。

**改动点**：
- 根据驱动/发布器状态判断：全 Running → healthy，有 Error → degraded，全 Error → unhealthy
- 可加入 Pipeline PendingCount 阈值判断

---

## 中优先级

### 4. 请求模型缺少输入校验

**现状**：`CreateDriverRequest`、`CreatePublisherRequest` 等无字段长度/格式限制。

```csharp
public sealed class CreateDriverRequest
{
    public required string Protocol { get; init; }
    public required string InstanceId { get; init; }
    public Dictionary<string, object?>? Settings { get; init; }
}
```

**问题**：
- `Protocol`/`InstanceId` 无长度限制，可传入空格或超长字符串
- `Settings` 可传空字典
- `CreateTransformRuleRequest.ParametersJson` 名字含 "Json" 但无 JSON 格式校验
- `CreateRouteRuleRequest.TargetPublisherIds` 可传空列表

**改动点**：
- 加 DataAnnotation（`[MinLength]`、`[MaxLength]`、`[Required]`）或手动校验
- 关键字段加正则校验（如 InstanceId 格式）

---

### 5. Settings 序列化再反序列化，多余一圈

**现状**：客户端传 JSON → ASP.NET Core 反序列化为 `Dictionary` → 再序列化为 JSON 字符串 → 再解析为 `IConfiguration`。

```csharp
var settingsJson = System.Text.Json.JsonSerializer.Serialize(request.Settings);
var config = GatewayRuntime.BuildConfigurationFromJsonPublic(settingsJson);
```

**问题**：多余的序列化/反序列化开销，且 `BuildConfigurationFromJsonPublic` 这个命名有异味。

**改动点**：
- `AddDriverAsync` / `AddPublisherAsync` 直接接受 JSON 字符串或 `Dictionary`
- 将 JSON↔IConfiguration 转换逻辑抽到独立工具类

---

### 6. 转换/路由规则的 Request→Domain 手动映射可消除

**现状**：手动逐字段赋值，字段多了容易漏。

```csharp
var rule = new TransformRule
{
    RuleId = request.RuleId,
    Enabled = request.Enabled,
    TagPattern = request.TagPattern,
    Type = request.Type,
    ParametersJson = request.ParametersJson,
    Priority = request.Priority
};
```

**问题**：维护成本高，新增字段时容易遗漏。

**改动点**：
- 让 Request 模型直接复用 Domain 类型
- 或用 Mapster 等映射库
- 或让 endpoint 参数直接绑定 `TransformRule`

---

### 7. `/metrics` 不在 `/api` 组下，路径不一致

**现状**：`app.MapGet("/metrics", ...)` 在根路径，其他端点都在 `/api/*` 下。

**问题**：API 文档不完整，中间件策略不一致。

**改动点**：
- 保持 `/metrics` 在根路径（Prometheus 标准），但加注释说明原因
- 或增加 `/api/metrics` 别名

---

### 8. 异常处理不一致

**现状**：驱动端点有 409/404 处理，转换/路由规则端点缺少。

| 端点 | `InvalidOperationException` | `KeyNotFoundException` | 其他异常 |
|------|---|---|---|
| POST /drivers | 409 Conflict | — | 500 Problem |
| DELETE /drivers | — | 404 NotFound | 500 Problem |
| POST /transforms | — | — | 500 Problem（无 409） |
| DELETE /transforms | — | — | 500 Problem（无 404） |

**问题**：重复规则无 409，不存在规则无 404，客户端无法区分错误类型。

**改动点**：
- 为转换/路由规则端点补充 409 和 404 异常处理
- 统一异常处理策略

---

### 9. `/api/config/export` 直接暴露 `ConfigRepo`，绕过 Runtime 封装

**现状**：API 层直接调用 `runtime.ConfigRepo.ExportToJsonAsync()`。

```csharp
var json = await runtime.ConfigRepo.ExportToJsonAsync();
```

**问题**：绕过 Runtime 封装，API 层直接操作持久化层。

**改动点**：
- 将导入/导出功能封装为 `GatewayRuntime` 的方法
- 导入后自动触发重载，避免运行时与持久化不一致

---

## 低优先级

### 10. 插件信息返回匿名对象，无 API 契约

**现状**：返回 `new { protocol, name, version, description }` 匿名对象。

**问题**：无法生成 OpenAPI 文档，客户端没有类型契约。

**改动点**：定义 `PluginInfoResponse` 等具体返回类型。

---

### 11. Request 模型与 Domain 模型放在同一文件

**现状**：`CreateDriverRequest`、`CreatePublisherRequest` 等与 `ApiEndpoints` 类放在同一文件（294-329 行）。

**问题**：文件过长，职责混杂。

**改动点**：Request 模型移到独立的 `Requests/` 目录或文件中。

---

### 12. 全部端点写在一个方法中，330 行，可按领域拆分

**现状**：`MapGatewayApi` 一个方法包含驱动、发布器、插件、健康检查、配置、转换、路由、关机共 8 组端点。

**问题**：可读性差，修改一个领域容易误改另一个。

**改动点**：拆分为 `MapDriverEndpoints(api)`、`MapPublisherEndpoints(api)` 等，`MapGatewayApi` 只做编排。
