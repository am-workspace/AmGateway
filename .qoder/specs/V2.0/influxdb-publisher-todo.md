# InfluxDB Publisher 优化清单

> 发布器路径：`Plugins/AmGateway.Publisher.InfluxDB/InfluxDbPublisher.cs`  
> 审查时间：2026-04-29

---

## 严重问题（高优先级）

### 🔴 P0 — Timer 同步调用 `FlushBatchAsync` 导致并发 flush

**位置：** `InfluxDbPublisher.cs` 行 95~96

**现状：**

```csharp
_flushTimer = new Timer(_ => FlushBatchAsync().GetAwaiter().GetResult(),
    null, TimeSpan.FromMilliseconds(_config.FlushIntervalMs), TimeSpan.FromMilliseconds(_config.FlushIntervalMs));
```

- `GetAwaiter().GetResult()` 阻塞线程池线程
- 如果一次 flush 耗时超过间隔，**多个 flush 会并发执行**
- 并发时：一个线程快照了数据，另一个线程也快照（可能得到空数据），产生不必要的 HTTP 请求
- 同时 `_httpClient` 被多个线程并发调用有风险

**改动方案：** 改用 `PeriodicTimer` + `SemaphoreSlim` 防止并发：

```csharp
private readonly SemaphoreSlim _flushLock = new(1, 1);

private async Task RunFlushLoopAsync(CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.FlushIntervalMs));
    while (await timer.WaitForNextTickAsync(ct))
    {
        await FlushBatchAsync();
    }
}
```

**执行顺序：** 1

---

### 🔴 P0 — Gzip 压缩半实现

**位置：** `InfluxDbPublisher.cs` 行 69~72

**现状：**

```csharp
// 只设置了 Accept-Encoding（服务端响应压缩），请求体未压缩
_httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");
```

InfluxDB 支持请求体 gzip 压缩，对大 batch 能节省大量带宽。当前只告诉服务端返回 gzip，但上行数据未压缩。

**改动方案：**

```csharp
private static byte[] CompressGzip(string payload)
{
    var bytes = Encoding.UTF8.GetBytes(payload);
    using var ms = new MemoryStream();
    using (var gzip = new GZipStream(ms, CompressionLevel.Fastest))
    {
        gzip.Write(bytes);
    }
    return ms.ToArray();
}
```

发送时使用：

```csharp
var compressed = CompressGzip(payload);
var content = new ByteArrayContent(compressed);
content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
content.Headers.ContentEncoding.Add("gzip");
```

**执行顺序：** 2

---

### 🔴 P0 — 写入失败时数据直接丢弃

**位置：** `InfluxDbPublisher.cs` 行 194~199

**现状：**

InfluxDB 返回 4xx/5xx 错误时，仅记录日志，缓冲区中的数据被丢弃，没有任何重试或兜底。

**改动方案：**

```csharp
private void RequeueFailedData(List<string> failedData)
{
    lock (_batchLock)
    {
        _batchBuffer.InsertRange(0, failedData);  // 插回头部，下次重试
    }
}

// 在 FlushBatchAsync 中使用
if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent)
{
    var errorBody = await response.Content.ReadAsStringAsync();
    _logger.LogError("[{PublisherId}] InfluxDB 写入失败: {StatusCode} - {Error}",
        PublisherId, (int)response.StatusCode, errorBody);
    RequeueFailedData(toFlush);  // 重新排队
}
```

**执行顺序：** 3

---

### 🔴 P0 — `PublishAsync` 中 flush 缺少并发控制

**位置：** `InfluxDbPublisher.cs` 行 126~129

**现状：**

```csharp
if (shouldFlush)
{
    return new ValueTask(FlushBatchAsync());  // 和 Timer 的 flush 可能同时跑
}
```

Pipeline 消费线程在批满时触发 flush，和 Timer 周期的 flush 可能同时执行，产生并发冲突。

**改动方案：** 合并到同一并发控制：

```csharp
if (shouldFlush)
{
    await _flushLock.WaitAsync(ct);
    try { await FlushBatchAsync(); }
    finally { _flushLock.Release(); }
}
```

或者如果改用 `RunFlushLoopAsync` + `SemaphoreSlim`，这里也共享同一个锁。

**执行顺序：** 4

---

## 中等问题（中优先级）

### 🟡 P1 — 缓冲区无上限

**位置：** `InfluxDbPublisher.cs` 行 28

**现状：**

```csharp
private readonly List<string> _batchBuffer = [];
```

如果 InfluxDB 长时间不可用，`_batchBuffer` 会无限增长直到 OOM。

**改动方案：** 设置最大缓冲数：

```csharp
private const int MaxBufferSize = 100_000;

private void AddToBuffer(string line)
{
    lock (_batchLock)
    {
        if (_batchBuffer.Count >= MaxBufferSize)
        {
            _logger.LogWarning("[{PublisherId}] 缓冲区已满 ({MaxBufferSize})，丢弃最早的数据",
                PublisherId, MaxBufferSize);
            _batchBuffer.RemoveAt(0);
        }
        _batchBuffer.Add(line);
    }
}
```

**执行顺序：** 5

---

### 🟡 P2 — `value=null` 作为字符串写入

**位置：** `InfluxDbPublisher.cs` 行 274~276

**现状：**

```csharp
sb.Append("value=null");  // InfluxDB 会存储为字符串 "null"
```

InfluxDB Line Protocol 没有原生 `null` 类型，字符串 `"null"` 会被当作普通字符串存储，在查询时会显示为 textual value `"null"`，容易误导。

**改动方案：** 当值为 null 时跳过整条 line：

```csharp
if (point.Value == null)
{
    _logger.LogDebug("跳过 null 值数据点: {Tag}", point.Tag);
    return string.Empty;  // 调用方跳过空字符串
}
```

**执行顺序：** 6

---

### 🟡 P3 — `SocketsHttpHandler` 未释放

**位置：** `InfluxDbPublisher.cs` 行 56~60 + 行 155~159

**现状：**

```csharp
var handler = new SocketsHttpHandler { ... };  // 从未释放
_httpClient = new HttpClient(handler);
```

`HttpClient.Dispose()` 不自动释放传入的 `HttpHandler`，handler 内部维护的 TCP 连接池会泄漏。

**改动方案：**

```csharp
private SocketsHttpHandler? _handler;

public Task StartAsync(CancellationToken ct = default)
{
    _handler = new SocketsHttpHandler { ... };
    _httpClient = new HttpClient(_handler);
    // ...
}

public async ValueTask DisposeAsync()
{
    await StopAsync();
    _httpClient?.Dispose();
    _handler?.Dispose();  // 释放 handler
}
```

**执行顺序：** 7

---

### 🟡 P4 — `ReconnectDelayMs` 配置字段未使用

**位置：** `InfluxDbPublisherConfig.cs` 行 16

**现状：**

```csharp
public int ReconnectDelayMs { get; set; } = 5000;  // 从未在代码中被引用
```

从驱动模板复制过来的残留字段，建议移除以免误导。

**改动方案：** 直接从配置类中删除。

**执行顺序：** 8

---

## 轻微问题（低优先级）

### 🟢 P5 — 健康检查在 `StartAsync` 中阻塞启动

**位置：** 行 74~92

健康检查失败不会阻止启动（设计正确），但在 InfluxDB 启动慢的场景下，每次网关重启都会打印一条 Warning。建议改为延迟执行或仅在首次写入时做惰性检查。

### 🟢 P6 — `BatchSize` 和 `FlushIntervalMs` 合理但无文档

两个配置项的组合行为：

```
Flush 时机 = 缓冲数 >= BatchSize 或 距离上次 flush >= FlushIntervalMs（先到先触发）
```

可在配置 summary 或代码注释中补充说明。

---

## 推荐执行顺序

| 顺序 | 编号 | 优先级 | 问题 | 风险等级 |
|------|------|--------|------|----------|
| 1 | P0-1 | 严重 | Timer 同步调用导致并发 flush | 数据紊乱 |
| 2 | P0-2 | 严重 | Gzip 压缩半实现 | 带宽浪费 |
| 3 | P0-3 | 严重 | 写入失败数据丢弃 | 数据丢失 |
| 4 | P0-4 | 严重 | PublishAsync 中 flush 并发 | 竞态 |
| 5 | P1-1 | 中等 | 缓冲区无上限 | OOM |
| 6 | P2-1 | 中等 | `value=null` 存为字符串 | 数据误导 |
| 7 | P3-1 | 中等 | `SocketsHttpHandler` 未释放 | 连接池泄漏 |
| 8 | P4-1 | 中等 | `ReconnectDelayMs` 未使用 | 配置误导 |
| 9 | P5-1 | 轻微 | 健康检查位置 | 可控 |
| 10 | P6-1 | 轻微 | 配置项行为说明 | 可读性 |
