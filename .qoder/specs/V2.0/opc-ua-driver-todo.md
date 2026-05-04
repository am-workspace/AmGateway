# OPC UA Driver 优化清单

> 驱动路径：`Plugins/AmGateway.Driver.OpcUa/OpcUaDriver.cs`  
> 审查时间：2026-04-29

---

## 严重问题（高优先级）

### 🔴 P0 — `async void` 事件处理器

**位置：** `OpcUaDriver.cs` 行 236，事件处理器 `OnMonitoredItemNotification`

**现状：**

```csharp
private async void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
```

`async void` 是 C# 中最危险的模式之一。一旦该方法内部抛出未捕获的异常，会直接在进程中引发不可恢复的异常，导致进程崩溃。

**改动方案：**

```csharp
private void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
{
    _ = Task.Run(async () =>
    {
        try { await HandleNotificationAsync(item, e); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{DriverId}] 处理通知失败", DriverId);
        }
    });
}
```

**执行顺序：** 1

---

### 🔴 P0 — SDK 线程跨线程调用

**位置：** `OpcUaDriver.cs` 行 239~241，`OnMonitoredItemNotification` 中直接 `await _dataSink.PublishAsync`

**现状：**

`OnMonitoredItemNotification` 由 OPC UA SDK 的内部线程调用，在该线程上直接 `await _dataSink.PublishAsync` 存在以下风险：

- `_dataSink` 内部存在状态竞争
- 如果 `_dataSink` 有线程亲和性检查会抛异常
- `StopAsync` 正在释放资源时，SDK 线程仍在调用

**改动方案：** 使用 Channel 队列中转，解除对 SDK 线程的依赖。

```csharp
// 1. 声明 Channel
private readonly Channel<DataPoint> _notificationChannel = Channel.CreateUnbounded<DataPoint>();

// 2. SDK 线程只负责写入 Channel，不做异步操作
private void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
{
    try
    {
        var dataPoint = ExtractDataPoint(item, e);
        _notificationChannel.Writer.TryWrite(dataPoint);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "[{DriverId}] 提取通知数据失败", DriverId);
    }
}

// 3. 驱动内启动消费循环（由 StartAsync 调用）
private async Task ConsumeNotificationsAsync(CancellationToken ct)
{
    await foreach (var dp in _notificationChannel.Reader.ReadAllAsync(ct))
    {
        await _dataSink.PublishAsync(dp, ct);
    }
}
```

**执行顺序：** 2（依赖 P0-1）

---

### 🔴 P0 — 缺少 Session 断线重连机制

**位置：** `OpcUaDriver.cs` 行 151~163，`StartAsync` 只连接一次

**现状：**

OPC UA 是长连接，当 Server 重启、网络中断或 Session 超时，`_session` 失效但驱动不会感知，不会重新连接，数据永久中断。

**改动方案：** 订阅 Session 事件，并实现重连逻辑。

```csharp
// 在 ConnectAsync 中订阅
_session.KeepAlive += OnSessionKeepAlive;
_session.SessionClosed += OnSessionClosed;

// Session 断开回调
private async void OnSessionClosed(Session session, SessionClosedEventArgs e)
{
    _logger.LogWarning("[{DriverId}] OPC UA Session 断开: {Reason}", DriverId, e.Status);
    _subscription?.Delete();
    _subscription = null;
    _session = null;

    // 实现指数退避重连
    while (true)
    {
        try
        {
            await Task.Delay(GetReconnectDelay(), CancellationToken.None);
            await ConnectAsync(CancellationToken.None);
            await CreateSubscriptionAsync(CancellationToken.None);
            _logger.LogInformation("[{DriverId}] OPC UA 重连成功", DriverId);
            break;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{DriverId}] 重连失败，等待下次重试", DriverId);
        }
    }
}
```

**执行顺序：** 3

---

## 中等问题（中优先级）

### 🟡 P1 — `SecurityPolicy` 配置字段未使用

**位置：** `OpcUaDriverConfig` 定义了 `SecurityPolicy`，但行 142 选择端点时硬编码。

**现状：**

```csharp
// 配置类
public string SecurityPolicy { get; set; } = "None";  // 用户配置了不生效

// 连接时
var endpointDescription = endpoints.FirstOrDefault(e => e.SecurityPolicyUri == SecurityPolicies.None)
    ?? endpoints.First();  // 无视配置，兜底选第一个
```

用户配置 `SecurityPolicy: "Basic256"` 不会生效，容易产生误解。

**改动方案（二选一）：**

1. **实现它**（推荐）：

```csharp
var targetPolicy = _config.SecurityPolicy.ToLowerInvariant() switch
{
    "none" => SecurityPolicies.None,
    "basic128" => SecurityPolicies.Basic128,
    "basic256" => SecurityPolicies.Basic256,
    "basic256sha256" => SecurityPolicies.Basic256Sha256,
    _ => SecurityPolicies.None
};

var endpointDescription = endpoints.FirstOrDefault(e => e.SecurityPolicyUri == targetPolicy)
    ?? throw new InvalidOperationException($"未找到安全策略 {_config.SecurityPolicy} 的端点");
```

2. **移除字段**，避免用户误配置。

**执行顺序：** 4

---

### 🟡 P1 — 超时硬编码，未配置化

**位置：** 行 60、143、147

**现状：**

```csharp
ClientConfiguration = new ClientConfiguration
{
    DefaultSessionTimeout = 60000,  // 定义在配置类中
    // ...
}

sessionFactory.CreateAsync(..., 60000, ...)  // 但这里又硬编码 60000
```

两处不一致，且 `ClientConfiguration.DefaultSessionTimeout` 未实际使用。

**改动方案：**

```csharp
// OpcUaDriverConfig 增加字段
public int SessionTimeoutMs { get; set; } = 60000;

// 行 143
sessionFactory.CreateAsync(..., _config.SessionTimeoutMs, ...)

// 行 60
DefaultSessionTimeout = _config.SessionTimeoutMs
```

**执行顺序：** 5

---

### 🟡 P1 — `AutoAcceptUntrustedCertificates = true` 生产环境风险

**位置：** 行 56

**现状：**

```csharp
SecurityConfiguration = new SecurityConfiguration
{
    AutoAcceptUntrustedCertificates = true,  // ← 生产环境不安全
}
```

生产环境应验证证书。建议改为可配置，默认为 `false`：

```csharp
public bool AutoAcceptUntrustedCertificates { get; set; } = false;
```

**执行顺序：** 6

---

### 🟡 P1 — `ActivitySource`/`Meter` 每实例重复创建

**位置：** 行 7~8，`NullTelemetryContext`

**现状：**

```csharp
public ActivitySource ActivitySource { get; } = new("AmGateway.OpcUa");
public Meter CreateMeter() => new("AmGateway.OpcUa");
```

`ActivitySource` 和 `Meter` 是进程级单例，每个驱动实例都 new 一个会有资源浪费和重复注册问题。

**改动方案：**

```csharp
file sealed class NullTelemetryContext : ITelemetryContext
{
    public static readonly ActivitySource ActivitySource = new("AmGateway.OpcUa");
    public static readonly Meter Meter = new("AmGateway.OpcUa");

    public ActivitySource CreateActivitySource() => ActivitySource;
    public Meter CreateMeter() => Meter;
}
```

**执行顺序：** 7

---

## 轻微问题（低优先级）

### 🟢 P2 — `QueueSize = 10` 未配置化

**位置：** 行 167，`CreateSubscriptionAsync` 中 `QueueSize = 10`

当采集频率高于消费速度时，队列溢出可能导致数据丢失。移到配置中更灵活。

**改动方案：** `OpcUaDriverConfig` 增加 `int QueueSize { get; set; } = 10`。

---

### 🟢 P2 — `NullLogger` 完全丢弃日志

**位置：** 行 27~28

```csharp
public bool IsEnabled(LogLevel logLevel) => false;  // 所有日志被吞掉
```

OPC UA SDK 内部的重要警告（证书即将过期等）用户完全看不到。

**改动方案：**

```csharp
public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
```

---

### 🟢 P2 — 缺少 Subscription 生命周期事件

**位置：** `CreateSubscriptionAsync`

建议订阅 `Subscription.Notification` 和 `Subscription.StatusChanged` 事件，以便在订阅创建失败或状态变化时能感知和上报。

---

## 推荐执行顺序

| 顺序 | 编号 | 优先级 | 问题 | 风险等级 |
|------|------|--------|------|----------|
| 1 | P0-1 | 严重 | `async void` 事件处理器 | 进程崩溃 |
| 2 | P0-2 | 严重 | SDK 线程跨线程调用 | 竞态/死锁 |
| 3 | P0-3 | 严重 | Session 无重连 | 断线永久失效 |
| 4 | P1-1 | 中等 | `SecurityPolicy` 未实现 | 配置欺骗用户 |
| 5 | P1-2 | 中等 | 超时硬编码 | 配置不一致 |
| 6 | P1-3 | 中等 | `AutoAcceptUntrustedCertificates` | 生产安全风险 |
| 7 | P1-4 | 中等 | `ActivitySource`/`Meter` 重复创建 | 资源浪费 |
| 8 | P2-1 | 轻微 | `QueueSize` 未配置化 | 调参不便 |
| 9 | P2-2 | 轻微 | `NullLogger` 吞日志 | 问题难排查 |
| 10 | P2-3 | 轻微 | 缺少 Subscription 事件 | 状态感知差 |
