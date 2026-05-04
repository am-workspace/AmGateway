# S7 Driver 优化清单

> 驱动路径：`Plugins/AmGateway.Driver.S7/S7Driver.cs`  
> 审查时间：2026-04-29

---

## 严重问题（高优先级）

### 🔴 P0 — 重连时旧 Plc 对象未释放 — Socket 泄漏

**位置：** `S7Driver.cs` 行 122~131，`EnsureConnectedAsync`

**现状：**

```csharp
private async Task EnsureConnectedAsync(CancellationToken ct)
{
    if (_plc is { IsConnected: true }) return;

    var cpuType = Enum.Parse<CpuType>(_config.CpuType, ignoreCase: true);
    _plc = new Plc(cpuType, _config.IpAddress, _config.Rack, _config.Slot);  // 直接覆盖，旧的未 Close
    await _plc.OpenAsync(ct);
}
```

`EnsureConnectedAsync` 被 `RunPollingAsync` 的异常分支调用（行 89），此时 `_plc` 可能还持有上一个失败连接的 TCP Socket。直接 `new Plc()` 覆盖旧引用会导致旧对象永远无法被 `Close()`，Socket 泄漏。

**改动方案：**

```csharp
private async Task EnsureConnectedAsync(CancellationToken ct)
{
    if (_plc is { IsConnected: true }) return;

    var cpuType = Enum.Parse<CpuType>(_config.CpuType, ignoreCase: true);
    Disconnect();  // 先断开旧的
    _plc = new Plc(cpuType, _config.IpAddress, _config.Rack, _config.Slot);
    await _plc.OpenAsync(ct);
}
```

**执行顺序：** 1

---

### 🔴 P0 — `CpuType` 无校验 — 配置错误会抛运行时异常

**位置：** `S7Driver.cs` 行 126

**现状：**

```csharp
var cpuType = Enum.Parse<CpuType>(_config.CpuType, ignoreCase: true);
```

如果配置了 `CpuType: "S7300"`，实际枚举值可能是 `S7_300`（带下划线），`Enum.Parse` 会抛 `ArgumentException`，且异常消息不会告诉用户哪个值是对的。

**改动方案：** 在 `InitializeAsync` 中预解析并校验：

```csharp
public Task InitializeAsync(DriverContext context, CancellationToken ct = default)
{
    // ...existing code...

    if (!Enum.TryParse<CpuType>(_config.CpuType, ignoreCase: true, out var cpuType))
    {
        throw new InvalidOperationException(
            $"S7 驱动配置错误：CpuType \"{_config.CpuType}\" 无效。" +
            $"有效值为: {string.Join(", ", Enum.GetNames<CpuType>())}");
    }

    // ...rest...
}
```

**执行顺序：** 2

---

### 🔴 P0 — `Plc.OpenAsync` 无超时 — 网络故障时永久阻塞

**位置：** `S7Driver.cs` 行 128

**现状：**

S7.Net 库的 `OpenAsync` 内部是 TCP 连接，没有超时保护。如果 PLC IP 不通，会等很久（取决于系统 TCP 超时，通常几十秒）。

**改动方案：** 用 `Task.Run` + 超时包装：

```csharp
private async Task EnsureConnectedAsync(CancellationToken ct)
{
    if (_plc is { IsConnected: true }) return;

    var cpuType = Enum.Parse<CpuType>(_config.CpuType, ignoreCase: true);
    Disconnect();
    _plc = new Plc(cpuType, _config.IpAddress, _config.Rack, _config.Slot);

    using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectCts.Token);

    try
    {
        await _plc.OpenAsync(linked.Token);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        _logger.LogWarning("[{DriverId}] S7 连接超时（5秒）", DriverId);
        Disconnect();
        throw new TimeoutException("S7 连接超时");
    }
}
```

**执行顺序：** 3（依赖 P0-1）

---

## 中等问题（中优先级）

### 🟡 P1 — `DataItems` 逐条读取 — 效率低下

**位置：** `S7Driver.cs` 行 137~172，`ReadDataItemsAsync`

**现状：**

```csharp
foreach (var item in _config.DataItems)
{
    var value = await ReadSingleItemAsync(item);  // 每个 item 一次 S7 请求
}
```

如果配置了 20 个数据项，每个轮询周期要发 20 次 S7 请求。S7 协议支持在同一个 DB 内批量读取连续/不连续区域，应该按 DB 分组，批量读取后再拆分。

**优化方案：**

```
当前：20个数据项 → 20次 S7 Read 请求

优化：同一个 DB 的数据 → 1次批量 Read → 拆分解析
```

1. 按 `DbNumber` 分组数据项
2. 对每个 DB，一次读取该 DB 的所有字节范围
3. 在内存中按 `StartByte` 和 `VarType` 拆分解析

**执行顺序：** 4

---

### 🟡 P2 — `Count` 和 `BitIndex` 未使用

**位置：** `S7Driver.cs` 行 175~181

**现状：**

```csharp
// 行 179
var result = await _plc!.ReadAsync(dataType, item.DbNumber, item.StartByte, varType, item.Count);
//          ↑ 传了 item.Count，但 s7.net 的 ReadAsync 的 Count 参数含义需要确认

// item.BitIndex 完全未使用
```

如果要读 Bool 类型的某一位（不是整个字节），需要额外处理位提取。

**建议：** 确认 `S7.Net` 的 `ReadAsync` 方法是否支持 `Count > 1` 的数组返回，如果是返回数组，则 `ReadDataItemsAsync` 需要增加对数组的处理逻辑。

**执行顺序：** 5

---

### 🟡 P3 — 缺少重连指数退避

**现状：**

固定 `ReconnectDelayMs`，频繁断连时会反复重试，对 PLC 和网络造成压力。

**改动方案：**

```csharp
private int _reconnectAttempts = 0;

private int GetReconnectDelay()
{
    var delay = Math.Min(_config.ReconnectDelayMs * (1 << _reconnectAttempts), 60_000);
    _reconnectAttempts++;
    return delay;
}

// 在连接成功后重置
private void OnConnectSuccess() => _reconnectAttempts = 0;
```

---

### 🟡 P4 — `Disconnect()` 可能抛异常

**位置：** `S7Driver.cs` 行 211~218

**现状：**

```csharp
private void Disconnect()
{
    if (_plc != null)
    {
        _plc.Close();  // 可能抛异常，StopAsync 未捕获
    }
}
```

`Plc.Close()` 如果底层 Socket 异常会抛出，`StopAsync` 会被中断。

**改动方案：**

```csharp
private void Disconnect()
{
    try
    {
        _plc?.Close();
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "[{DriverId}] S7 断开连接时发生异常", DriverId);
    }
    finally
    {
        _plc = null;
    }
}
```

**执行顺序：** 6

---

### 🟡 P5 — 默认 `CpuType` 与库枚举值可能不匹配

**位置：** `S7DriverConfig` 行 224

**现状：**

```csharp
public string CpuType { get; set; } = "S71200";  // 如果库的实际枚举是 "S7_1200" 会出问题
```

需要确认 `S7.Net` 中 `CpuType` 枚举的实际值名，并据此设置正确的默认值。

**建议：** 参考 [S7.Net CpuType 枚举](https://github.com/S7Python/s7netplus) 的实际定义。

---

## 推荐执行顺序

| 顺序 | 编号 | 优先级 | 问题 | 风险等级 |
|------|------|--------|------|----------|
| 1 | P0-1 | 严重 | 重连时旧 Plc 未释放 | Socket 泄漏 |
| 2 | P0-2 | 严重 | `CpuType` 无校验 | 运行时崩溃 |
| 3 | P0-3 | 严重 | `OpenAsync` 无超时 | 永久阻塞 |
| 4 | P1-1 | 中等 | 逐条读取效率低 | 性能浪费 |
| 5 | P1-2 | 中等 | `Count`/`BitIndex` 未使用 | 功能不完整 |
| 6 | P1-3 | 中等 | 无指数退避重连 | 频繁断连时资源浪费 |
| 7 | P1-4 | 中等 | `Disconnect` 可能抛异常 | 停止流程中断 |
| 8 | P1-5 | 中等 | 默认值与库枚举对齐 | 配置错误风险 |
