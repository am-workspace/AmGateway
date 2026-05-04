# 网关运行时（GatewayRuntime）待办事项

> 当前完成度约 70%，核心 CRUD 与持久化已闭环，以下为需要改进的问题。

---

## 高优先级

### 1. AddPublisherAsync 异常时 Pipeline 未清理，产生孤儿发布器

**现状：** `AddPublisherAsync` 中，Publisher 成功启动后注册到 Pipeline，但如果后续持久化失败进入 catch，只从 `_publishers` 字典移除，未从 Pipeline 移除。

```csharp
// 成功后注册
_pipeline.AddPublisher(publisher);

// catch 中只清理字典
catch (Exception ex)
{
    _publishers.TryRemove(instanceId, out _);
    // ❌ 缺少 _pipeline.RemovePublisher(publisher)
}
```

**问题：** 发布器从字典消失但仍在 Pipeline 中接收数据，成为无法通过 API 管理的"孤儿发布器"。`AddDriverAsync` 不存在此问题（驱动不直接注册到 Pipeline）。

**改动点：** catch 块中增加 `_pipeline.RemovePublisher(publisher)`，需将 `publisher` 变量提前声明以便 catch 中可访问。

---

### 2. Remove 先删字典再停实例，错误状态丢失

**现状：** `RemoveDriverAsync` / `RemovePublisherAsync` 先 `TryRemove` 从字典移除，再执行 `StopAsync`。如果 Stop 失败，设置了 `Error` 状态但 entry 已不在字典中。

```csharp
if (!_drivers.TryRemove(instanceId, out var entry))  // 先移除
    ...
try
{
    await entry.Driver.StopAsync(ct);                // 后停止
}
catch (Exception ex)
{
    entry.Info.Status = RuntimeStatus.Error;         // ← 设了状态
    // ❌ 但 entry 已不在字典里，没人能读到
}
```

**问题：** Stop 失败时，驱动/发布器可能仍在运行（连接未断），但已从字典消失——既无法管理，也看不到错误状态。

**改动点：** 改为"先 Stop 成功再 Remove"模式，或 Stop 失败时将 entry 重新放回字典。

---

### 3. RestartDriverAsync 静默返回，不抛异常

**现状：** `RestartDriverAsync` 中创建驱动实例失败时，设置 Error 状态后直接 `return`，不抛异常。

```csharp
if (driver == null)
{
    entry.Info.Status = RuntimeStatus.Error;
    entry.Info.ErrorMessage = "无法创建驱动实例";
    return;   // ❌ 调用方无法知道重启失败了
}
```

**问题：** 对比 `AddDriverAsync` 同样场景是 `throw`。API 端点调用 `RestartDriverAsync` 时收到 200 OK，但实际重启失败了。Watchdog 调用时也无法感知失败。

**改动点：** 改为 `throw new InvalidOperationException(info.ErrorMessage)`，与 `AddDriverAsync` 行为一致。

---

### 4. 规则热更新存在并发竞态，可能丢失规则

**现状：** `AddTransformRuleAsync` / `AddRouteRuleAsync` 的热更新采用"读取 → 过滤 → 追加 → 写回"模式。

```csharp
var currentRules = _transformEngine.GetRules()       // ① 读取当前规则
    .Where(r => !string.Equals(r.RuleId, rule.RuleId, ...))
    .Append(rule)
    .ToList();
_transformEngine.LoadRules(currentRules);             // ② 写回引擎
```

**问题：** 两个并发 API 请求同时添加规则时，线程 A 在 ① 读到 N 条规则，线程 B 也读到 N 条规则，各自 append 后写回——后写的覆盖先写的，丢失一条规则。

**改动点：** 方案一：`ITransformEngine` / `IRouteResolver` 增加 `AddRule` / `RemoveRule` 原子方法，引擎内部保证线程安全。方案二：在 `GatewayRuntime` 层加锁（如 `SemaphoreSlim`）串行化规则变更。

---

## 中优先级

### 5. ConfigRepo 公开暴露，破坏封装

**现状：** `ConfigRepo` 属性公开返回 `_configRepo`，API 端点直接操作持久化层（导入/导出）。

```csharp
public IConfigRepository ConfigRepo => _configRepo;
```

**问题：** 绕过 Runtime 的管理，导入配置后不会自动重载驱动/发布器，可能导致运行时状态与持久化不一致。

**改动点：** 将导入/导出封装为 `GatewayRuntime` 的方法（如 `ExportConfigAsync` / `ImportConfigAsync`），导入后根据需要自动触发重载。

---

### 6. CancellationToken 参数声明了但未使用

**现状：** 规则管理方法声明了 `CancellationToken ct` 但未传递给底层调用。

```csharp
public async Task<TransformRule> AddTransformRuleAsync(TransformRule rule, CancellationToken ct)
{
    await _configRepo.SaveTransformRuleAsync(...)  // ❌ ct 未传递
```

**问题：** 长时间数据库操作无法被取消，客户端断开连接后服务端仍在执行。

**改动点：** 将 `ct` 传递给 `_configRepo` 的 `Save*Async` / `Delete*Async` 方法。需同步更新 `IConfigRepository` 接口签名。

---

### 7. ActiveDriverCount / ActivePublisherCount 命名误导

**现状：** 属性名含"Active"，但返回的是字典中所有条目的数量，包含 Starting、Error、Stopping 状态。

```csharp
public int ActiveDriverCount => _drivers.Count;
```

**问题：** 指标和日志中显示"活跃驱动 N 个"，但实际可能包含处于 Error 状态的实例，误导运维判断。

**改动点：** 改名为 `TotalDriverCount` / `TotalPublisherCount`，或改为只统计 `Status == Running` 的条目。若改过滤逻辑，需同步确认 `GatewayHostService` 指标循环的语义。

---

### 8. ConfigurationToJson 生成扁平键，非标准 JSON

**现状：** `ConfigurationToJson` 输出扁平格式，而非标准嵌套 JSON。

```csharp
// 输出：{"Host:Port": "502", "Host:Ip": "127.0.0.1"}
// 而非：{"Host": {"Port": "502", "Ip": "127.0.0.1"}}
```

**问题：** 持久化到 SQLite 的 JSON 不符合标准嵌套格式，人工查看/编辑时不直观。虽然 `BuildConfigurationFromJson` 能正确反序列化，但导出/导入流程中可读性差。

**改动点：** `ConfigurationToJson` 改为递归构建嵌套 `Dictionary`，再序列化为标准 JSON。或引入独立的 JSON ↔ IConfiguration 转换工具类。

---

### 9. GetDriverInfos / GetPublisherInfos 返回可变对象引用

**现状：** 返回的是 `Info` 对象的直接引用，而非副本。

```csharp
public IReadOnlyList<DriverInstanceInfo> GetDriverInfos() =>
    _drivers.Values.Select(e => e.Info).ToList();  // 返回引用
```

**问题：** 外部代码可直接修改 `Status`、`ErrorMessage` 等字段，破坏运行时状态一致性。

**改动点：** 返回 `Info` 的深拷贝（实现 `ICloneable` 或用 `record` 类型），或将 `Info` 属性改为 `init` 只允许构造时赋值。

---

### 10. RestartDriverAsync 不检查当前状态

**现状：** 文档注释说"重新启动一个处于 Error 状态的驱动"，但代码未校验当前状态。

**问题：** 对 Running 状态的驱动调用 restart 会导致先停再启，产生不必要的数据中断。对 Stopping 状态的驱动调用则行为未定义。

**改动点：** 方法开头校验 `entry.Info.Status`，仅允许 Error 状态重启；Running 状态应先 Stop 再 Restart；其他状态抛 `InvalidOperationException`。

---

## 低优先级

### 11. BuildConfigurationFromJsonPublic 是尴尬的包装方法

**现状：** 仅因为 `private` 方法不能被 API 层调用就加了个 `Public` 后缀的包装。

```csharp
public static IConfiguration BuildConfigurationFromJsonPublic(string json) => BuildConfigurationFromJson(json);
```

**问题：** 命名不规范，职责不清。JSON ↔ IConfiguration 转换是通用工具，不应挂在 `GatewayRuntime` 上。

**改动点：** 抽取为独立工具类（如 `ConfigurationJsonConverter`），`GatewayRuntime` 和 API 层共用。

---

### 12. DriverEntry 是 internal，PublisherEntry 是 private——不一致

**现状：** `DriverEntry` 标记为 `internal`（被 `DriverWatchdog` 访问），`PublisherEntry` 标记为 `private`。

**问题：** 同一层级的内部数据类访问修饰符不统一，增加理解成本。

**改动点：** 统一为 `internal`，或让 `DriverWatchdog` 通过 `GatewayRuntime` 的内部方法访问所需数据，将 `DriverEntry` 降为 `private`。

---

### 13. 驱动与发布器的增删启停代码高度重复

**现状：** `AddDriverAsync` / `AddPublisherAsync`、`RemoveDriverAsync` / `RemovePublisherAsync`、`Start*FromPersistenceAsync` 结构几乎一模一样。

**问题：** 如果后续加第三种实例类型（如"转发器"），重复代码会继续膨胀。Bug 修复也需同步到多处。

**改动点：** 可考虑泛型基类或模板方法模式抽取公共流程，具体驱动/发布器的差异通过委托/策略注入。优先级低，当前两类实例短期内不会大幅增加。

---

### 14. SeedFromAppSettingsAsync 的空检查不原子

**现状：** 先查持久化是否有数据，为空才种子。但检查与写入之间没有原子保证。

```csharp
var existingDrivers = await _configRepo.GetAllDriversAsync();
var existingPublishers = await _configRepo.GetAllPublishersAsync();
if (existingDrivers.Count > 0 || existingPublishers.Count > 0) return;
// 两个实例同时启动，都可能通过此检查
```

**问题：** 理论上如果两个进程同时启动，可能重复种子。实际上单实例部署不会触发，但架构上不严谨。

**改动点：** 加应用级互斥锁（`Mutex`），或让 `_configRepo.Save*Async` 幂等（INSERT OR REPLACE）。优先级低，当前部署模式无风险。
