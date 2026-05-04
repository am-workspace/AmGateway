# 网关主机服务（GatewayHostService）待办事项

> 当前完成度约 75%，核心启停流程已闭环，以下为需要改进的问题。

---

## 高优先级

### 1. StopAsync 与 ExecuteAsync 竞态风险

**现状：** `ExecuteAsync` 在后台线程运行指标更新循环，`StopAsync` 在另一个线程停止驱动/发布器，`base.StopAsync()` 放在最后。

**问题：** `stoppingToken` 触发后，指标循环可能尚未退出，而 `StopAsync` 已开始销毁驱动和发布器，存在访问已释放资源的风险。`base.StopAsync()` 的调用顺序反了——应先让 `ExecuteAsync` 退出，再执行清理。

**改动点：** `StopAsync` 开头先调用 `await base.StopAsync(cancellationToken)` 确保 `ExecuteAsync` 退出，再执行后续停机流程。

---

### 2. 停止阶段超时硬编码，总时长可能超限

**现状：** 每停一个驱动/发布器独立创建 `CancellationTokenSource(TimeSpan.FromSeconds(10))`，同时宿主有默认 30s 关机超时。

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await runtime.RemoveDriverAsync(kvp, cts.Token, removePersistence: false);
```

**问题：** 如果有 N 个驱动，总耗时上限为 `N × 10s`，可能超过宿主关机超时被强制杀死，导致数据丢失。

**改动点：** 使用共享 `CancellationTokenSource` 管理总超时，每个实例不再独立 10s，而是在剩余时间内尝试停止。

---

## 中优先级

### 3. 删除 GatewayRuntimeAccessor，改用构造函数注入

**现状：** `GatewayRuntimeAccessor` 是静态可变状态类，`GatewayHostService` 通过它获取 `GatewayRuntime`。

```csharp
var runtime = GatewayRuntimeAccessor.Runtime!;
```

**问题：** 隐式依赖，不利于测试，`Runtime` 可能为 `null`，`StopAsync` 中需要额外判空。API 端点通过 Minimal API 的 DI 参数绑定可直接注入 `GatewayRuntime`，不需要此静态类。

**改动点：** `GatewayHostService` 构造函数直接注入 `GatewayRuntime`，删除 `GatewayRuntimeAccessor` 类，`Program.cs` 中移除赋值语句。

---

### 4. 构造函数依赖过多，精简注入项

**现状：** 注入了 `PluginManager`, `IDataPipeline`, `ITransformEngine`, `IRouteResolver`, `GatewayMetrics`, `IConfiguration` 共 7 个服务。

**问题：** 其中大部分 `GatewayRuntime` 已持有，形成冗余的菱形依赖。如果改为注入 `GatewayRuntime`，只需保留 `GatewayMetrics` 和 `IConfiguration`。

**改动点：** 注入 `GatewayRuntime` 后，移除 `PluginManager`, `IDataPipeline`, `ITransformEngine`, `IRouteResolver` 等直接依赖，通过 `runtime` 间接访问。

---

### 5. 驱动/发布器串行停止，关机耗时长

**现状：** `StopAsync` 中用 `foreach` + `await` 逐个停止驱动和发布器。

```csharp
foreach (var kvp in ids)
{
    await runtime.RemoveDriverAsync(kvp, cts.Token, removePersistence: false);
}
```

**问题：** 驱动/发布器数量多时，串行停止耗时过长。

**改动点：** 改为 `Task.WhenAll` 并行停止，配合共享超时控制：

```csharp
var tasks = ids.Select(id => runtime.RemoveDriverAsync(id, cts.Token, false));
await Task.WhenAll(tasks);
```

---

## 低优先级

### 6. 管道排空使用 Task.Delay 轮询，不够优雅

**现状：** 排空阶段用 200ms 间隔轮询 `_pipeline.PendingCount`。

```csharp
while (_pipeline.PendingCount > 0 && drainSw.Elapsed < drainDeadline)
{
    await Task.Delay(200, cancellationToken);
}
```

**问题：** 高吞吐时 200ms 间隔反应慢，低吞吐时浪费 CPU 周期。

**改动点：** 让 `IDataPipeline` 提供 `WaitForDrainAsync(timeout)` 方法，基于 `Channel.Reader.Completion` 或 `SemaphoreSlim` 实现信号通知。

---

### 7. Pipeline 初始化顺序靠注释维系

**现状：** `SetPublishers`、`SetTransformEngine`、`SetRouteResolver` 在运行时手动调用，顺序由注释保证。

```csharp
_pipeline.SetPublishers(runtime.GetPublisherInstances());  // 4
_pipeline.SetTransformEngine(_transformEngine);              // 5
_pipeline.SetRouteResolver(_routeResolver);                   // 5
```

**问题：** 调整顺序可能导致数据走空管道，运行时初始化与构造时 DI 注入风格不一致。

**改动点：** `SetTransformEngine` 和 `SetRouteResolver` 移至 DI 配置阶段，或在 `ChannelDataPipeline` 构造函数中直接注入。

---

### 8. GatewayRuntime.StopAllAsync 与 StopAsync 逻辑重复

**现状：** `GatewayRuntime.StopAllAsync()` 封装了停止所有驱动+发布器的逻辑，但 `GatewayHostService.StopAsync` 手写了更细粒度的停机流程（含排空阶段）。

**问题：** `StopAllAsync` 成为死代码，两者逻辑可能不同步。

**改动点：** 要么让 `StopAsync` 复用 `StopAllAsync`（排空逻辑单独抽为方法），要么删除 `StopAllAsync`。
