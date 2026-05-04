# MQTT Publisher 优化清单

> 发布器路径：`Plugins/AmGateway.Publisher.Mqtt/MqttPublisher.cs`  
> 审查时间：2026-04-29

---

## 严重问题（高优先级）

### 🔴 P0 — `UseTls` 配置未生效

**位置：** `MqttPublisher.cs` 行 51~54

**现状：**

```csharp
var optionsBuilder = new MqttClientOptionsBuilder()
    .WithTcpServer(_config.Broker, _config.Port)  // 固定 TCP，无视 UseTls
    .WithClientId(_config.ClientId)
    .WithCleanSession();
```

配置类有 `UseTls` 字段（`MqttPublisherConfig.cs` 行 16），但代码中从未调用 `.WithTls()`。用户配置 `UseTls: true`，连接还是明文 TCP 1883，不会转成 TLS 8883，存在安全漏洞。

**改动方案：**

```csharp
var optionsBuilder = new MqttClientOptionsBuilder()
    .WithTcpServer(_config.Broker, _config.Port)
    .WithClientId(_config.ClientId)
    .WithCleanSession();

if (_config.UseTls)
{
    optionsBuilder.WithTls();
    _logger.LogInformation("[{PublisherId}] MQTT 使用 TLS 加密", PublisherId);
}
```

**执行顺序：** 1

---

### 🔴 P1 — 重连循环无上限

**位置：** `MqttPublisher.cs` 行 178~191

**现状：**

```csharp
for (var i = 0; ; i++)  // 无限循环，永不退出
{
    await _mqttClient!.ConnectAsync(_mqttOptions);
}
```

如果 Broker 永久不可用，这个循环永远不退出，一直占用线程池线程，日志持续刷 Warning。

**改动方案：** 增加最大重试次数 + 指数退避：

```csharp
const int MaxRetries = 10;
for (var i = 0; i < MaxRetries; i++)
{
    try
    {
        var delay = Math.Min(_config.ReconnectDelayMs * (1 << i), 60_000);
        await Task.Delay(delay);
        await _mqttClient!.ConnectAsync(_mqttOptions);
        _logger.LogInformation("[{PublisherId}] MQTT 重连成功 (第 {Attempt} 次)", PublisherId, i + 1);
        return;
    }
    catch
    {
        _logger.LogWarning("[{PublisherId}] MQTT 重连失败 (第 {Attempt} 次/{MaxRetries})", PublisherId, i + 1, MaxRetries);
    }
}
_logger.LogError("[{PublisherId}] MQTT 重连达到最大次数 ({MaxRetries})，放弃重连", PublisherId, MaxRetries);
```

**执行顺序：** 2

---

### 🔴 P2 — `PublishBatchAsync` 逐条发送，无批量

**位置：** `MqttPublisher.cs` 行 115~121

**现状：**

```csharp
public async ValueTask PublishBatchAsync(IReadOnlyList<DataPoint> points, CancellationToken ct = default)
{
    foreach (var point in points)
    {
        await PublishAsync(point, ct);  // 1000 个点 = 1000 次 MQTT 发布
    }
}
```

Pipeline 调用 `PublishBatchAsync` 时没有获得任何批量性能优势。

**改动方案：** 限制并发数并行发送：

```csharp
public async ValueTask PublishBatchAsync(IReadOnlyList<DataPoint> points, CancellationToken ct = default)
{
    const int MaxConcurrency = 10;
    using var semaphore = new SemaphoreSlim(MaxConcurrency);

    var tasks = points.Select(async point =>
    {
        await semaphore.WaitAsync(ct);
        try { await PublishAsync(point, ct); }
        finally { semaphore.Release(); }
    });

    await Task.WhenAll(tasks);
}
```

**执行顺序：** 3

---

### 🔴 P3 — `StopAsync` 与重连存在竞态

**位置：** `MqttPublisher.cs` 行 72~85 与 164~198

**现状：**

可能的时间线：

```
1. 断线触发 OnDisconnectedAsync，进入重连循环
2. StopAsync → 移除事件处理器 → 执行 DisconnectAsync
3. 重连循环中的 ConnectAsync 刚刚连上 Broker
4. 结果：StopAsync 完成后，_mqttClient 实际是 Connected 状态
```

**改动方案：** 在重连循环中检查停止标志：

```csharp
private bool _stopping;

public async Task StopAsync(CancellationToken ct = default)
{
    _stopping = true;
    // ...existing code...
}

// 重连循环中
for (var i = 0; i < MaxRetries && !_stopping; i++)
```

**执行顺序：** 4

---

## 中等问题（中优先级）

### 🟡 P4 — 断线期间数据直接丢弃

**位置：** `MqttPublisher.cs` 行 89~93

**现状：**

```csharp
if (_mqttClient == null || !_mqttClient.IsConnected)
{
    return;  // 静默丢弃
}
```

从断线到重连成功之间产生的所有数据都丢失。

**改动方案：** 使用 Channel 缓冲，重连后刷出：

```csharp
private readonly Channel<DataPoint> _pendingChannel =
    Channel.CreateBounded<DataPoint>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

// PublishAsync 中
if (!_mqttClient.IsConnected)
{
    await _pendingChannel.Writer.WriteAsync(point, ct);
    return;
}
```

**执行顺序：** 5

---

### 🟡 P5 — 无 KeepAlive 配置

**位置：** `MqttPublisher.cs` 行 51~54

MQTT Broker 依靠 KeepAlive 检测客户端存活性，当前没有显式设置。建议配置化：

```csharp
// OptionsBuilder 中
.WithKeepAlivePeriod(TimeSpan.FromSeconds(_config.KeepAliveSeconds))

// Config 中增加字段
public int KeepAliveSeconds { get; set; } = 30;
```

**执行顺序：** 6

---

### 🟡 P6 — `MqttClientFactory` 每次 `StartAsync` 都 new

**位置：** `MqttPublisher.cs` 行 48

```csharp
var factory = new MqttClientFactory();  // 无状态，可复用
```

**改动方案：**

```csharp
private static readonly MqttClientFactory Factory = new();
```

---

## 轻微问题（低优先级）

### 🟢 P7 — JSON Payload 用匿名类型，每次分配

**位置：** 行 146~158

```csharp
var payload = new { tag = point.Tag, ... };
return JsonSerializer.Serialize(payload);
```

可改为 `Dictionary<string, object?>` 或定义 record 类型减少 GC 分配。

### 🟢 P8 — Topic 使用 `ToLowerInvariant()` 可能影响下游匹配

**位置：** 行 140

```csharp
return $"{_config.TopicPrefix}/{point.SourceDriver}/{tagSuffix}".ToLowerInvariant();
```

MQTT Topic 区分大小写，如果下游订阅者严格匹配大小写，`ToLowerInvariant` 可能导致 topic 不匹配。建议去掉或作为配置项。

---

## 推荐执行顺序

| 顺序 | 编号 | 优先级 | 问题 | 风险等级 |
|------|------|--------|------|----------|
| 1 | P0-1 | 严重 | `UseTls` 配置未生效 | 安全漏洞 |
| 2 | P1-1 | 严重 | 重连循环无限 | 线程泄露 |
| 3 | P2-1 | 严重 | `PublishBatchAsync` 逐条 | 性能差 |
| 4 | P3-1 | 严重 | StopAsync 与重连竞态 | 停止后意外重连 |
| 5 | P4-1 | 中等 | 断线期间数据丢弃 | 数据丢失 |
| 6 | P5-1 | 中等 | 无 KeepAlive | 连接假死 |
| 7 | P6-1 | 中等 | `MqttClientFactory` 反复 new | 小资源浪费 |
| 8 | P7-1 | 轻微 | 匿名类型序列化 | 可忽略 |
| 9 | P8-1 | 轻微 | Topic ToLower 影响匹配 | 取决于下游 |
