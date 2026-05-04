# ChannelDataPipeline 优化清单

> 文件路径：`AmGateway/Pipeline/ChannelDataPipeline.cs`  
> 审查时间：2026-04-30

---

## 严重问题（高优先级）

### 🔴 P0 — 慢发布器阻塞整个流水线

**位置：** `ChannelDataPipeline.cs` 行 205~218

**现状：**

```csharp
foreach (var publisher in targets)
{
    await publisher.PublishAsync(transformed.Value, ct);  // 任意一个慢就全堵
}
```

消费循环是单线程串行的。如果某个发布器变慢（如 InfluxDB 网络抖动 HTTP 请求耗时 5s），后续所有数据点都得等它完成后才能处理。

**后果：**
- Channel 内数据积压，快速填满 10000 容量
- 新数据点被 `DropOldest` 策略丢弃
- 即使其他发布器很快（如 MQTT 本地），也一起被拖慢

**改动方案（并行扇出，推荐）：**

```csharp
var publishTasks = targets.Select(publisher =>
    PublishToSingleAsync(publisher, transformed.Value, ct));

await Task.WhenAll(publishTasks);

// 异常隔离封装
private async Task PublishToSingleAsync(IPublisher publisher, DataPoint point, CancellationToken ct)
{
    try
    {
        await publisher.PublishAsync(point, ct);
        _metrics.DataPointPublished();
    }
    catch (Exception ex)
    {
        _metrics.PublishError();
        _logger.LogError(ex, "[Pipeline] 发布器 {PublisherId} 处理数据点失败: {Tag}",
            publisher.PublisherId, point.Tag);
    }
}
```

**执行顺序：** 1

---

### 🔴 P0 — Transform/Resolve 异常未捕获 — 消费循环可能崩溃

**位置：** `ChannelDataPipeline.cs` 行 160~201

**现状：**

```csharp
transformed = _transformEngine.Transform(point);      // ← 抛异常 → 循环终止
targets = _routeResolver.Resolve(...);                // ← 抛异常 → 循环终止
```

如果转换引擎或路由解析器的实现有 bug，异常会逃逸出 `ConsumeLoopAsync`，导致消费循环终止。

**改动方案：** 包裹异常处理：

```csharp
// 转换阶段
DataPoint? transformed;
try
{
    transformed = _transformEngine?.Transform(point) ?? point;
}
catch (Exception ex)
{
    _logger.LogError(ex, "[Pipeline] 转换引擎处理失败: {Tag}", point.Tag);
    continue;  // 丢弃该数据点，保护流水线
}

// 路由阶段
IReadOnlyList<IPublisher> targets;
try
{
    targets = _routeResolver?.Resolve(transformed, snapshot) ?? snapshot;
}
catch (Exception ex)
{
    _logger.LogError(ex, "[Pipeline] 路由解析失败: {Tag}", transformed.Tag);
    continue;
}
```

**执行顺序：** 2

---

### 🔴 P0 — 发布器 `PublishAsync` 无独立超时

**位置：** `ChannelDataPipeline.cs` 行 209

**现状：**

```csharp
await publisher.PublishAsync(transformed.Value, ct);  // ct 只在 StopAsync 时取消
```

如果某个发布器内部实现有 bug（如 HTTP 请求不设超时），会永久阻塞消费线程。

**改动方案：** 每个发布器调用加独立超时：

```csharp
using var publishCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, publishCts.Token);

try
{
    await publisher.PublishAsync(point, linked.Token);
}
catch (OperationCanceledException) when (publishCts.IsCancellationRequested)
{
    _logger.LogWarning("[Pipeline] 发布器 {PublisherId} 发布超时", publisher.PublisherId);
    _metrics.PublishError();
}
```

**执行顺序：** 3

---

## 中等问题（中优先级）

### 🟡 P1 — 指标 `_metrics.DataPointPublished()` 计数方式有歧义

**位置：** `ChannelDataPipeline.cs` 行 210

**现状：**

```csharp
await publisher.PublishAsync(...);
_metrics.DataPointPublished();  // 每个发布器调用一次
```

如果 1 个数据点发到 3 个发布器，指标显示 `Published = 3`。对统计"成功发布的数据点数量"有歧义。

**建议：** 区分指标或改命名：

| 指标名 | 含义 |
|--------|------|
| `DataPointPublished` | 成功发布的**原始数据点**数（每个点只计 1 次） |
| `PublishAttempts` | 成功发布的**发布调用**次数（每个发布器各计 1 次） |

**执行顺序：** 4

---

### 🟡 P2 — Channel 容量硬编码

**位置：** `ChannelDataPipeline.cs` 行 45

```csharp
Channel.CreateBounded<DataPoint>(new BoundedChannelOptions(10000))
```

不同部署场景（边缘设备内存有限 vs 云端大吞吐）需要不同容量，建议配置化。

**改动方案：** 通过 `IConfiguration` 或构造函数参数注入容量：

```csharp
public ChannelDataPipeline(ILogger<ChannelDataPipeline> logger, GatewayMetrics metrics, IConfiguration config)
{
    var capacity = config.GetValue<int>("Pipeline:ChannelCapacity", 10000);
    _channel = Channel.CreateBounded<DataPoint>(new BoundedChannelOptions(capacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
}
```

**执行顺序：** 5

---

### 🟡 P3 — 无反压信号通知驱动

**位置：** `ChannelDataPipeline.cs` 行 248~256

**现状：**

Channel 满时只是 `DropOldest`，但驱动端（Modbus/S7 轮询循环）完全不知情，继续全速生产。如果发布端长期处理不过来，驱动端实际上一直在做**无用功**（读取了也会丢）。

**建议：** 当 Channel 积压超过阈值（如 80%）时，给驱动发送"反压"信号，让驱动降低轮询频率或跳过本轮。

**执行顺序：** 6

---

## 轻微问题（低优先级）

### 🟢 P4 — `transformed.Value` 的 `.Value` 冗余

**位置：** `ChannelDataPipeline.cs` 多处 `transformed.Value.Tag` 等

如果 `DataPoint` 是 class（引用类型），在 C# 可空引用类型中，null check 后编译器就知道非 null，可以直接 `transformed.Tag`。如果是 struct 才需要 `.Value`。

### 🟢 P5 — `StopAsync` 未处理 `ConsumeLoopAsync` 非取消异常

**位置：** `ChannelDataPipeline.cs` 行 126~136

```csharp
try { await _consumeTask; }
catch (OperationCanceledException) { }
// ← 如果 Transform/Resolve 抛异常，这里会重新抛出，StopAsync 以异常完成
```

如果消费循环因非取消异常终止，`StopAsync` 也会抛异常。

---

## 推荐执行顺序

| 顺序 | 编号 | 优先级 | 问题 | 风险等级 |
|------|------|--------|------|----------|
| 1 | P0-1 | 严重 | 慢发布器阻塞流水线 | 数据大面积丢失 |
| 2 | P0-2 | 严重 | Transform/Resolve 异常未捕获 | 消费循环崩溃 |
| 3 | P0-3 | 严重 | 发布器无独立超时 | 消费线程永久挂起 |
| 4 | P1-1 | 中等 | 指标计数歧义 | 监控失真 |
| 5 | P2-1 | 中等 | Channel 容量硬编码 | 调参不灵活 |
| 6 | P3-1 | 中等 | 无反压机制 | 驱动做无用功 |
| 7 | P4-1 | 轻微 | `.Value` 冗余 | 可读性 |
| 8 | P5-1 | 轻微 | StopAsync 未处理非取消异常 | 优雅关闭不完整 |
