# Modbus TCP 驱动（ModbusTcpDriver）待办事项

> 基础轮询采集功能已实现，以下为需要改进的问题和可选的进阶功能。

---

## 高优先级

### 1. TCP 连接缺少超时控制

**现状：** `ConnectAsync` 直接使用传入的 `CancellationToken`，依赖调用方控制超时。

```csharp
// 当前代码 - 网络不通时可能等待数十秒才超时
await _tcpClient.ConnectAsync(_config.Host, _config.Port, ct);
```

**问题：** 如果目标主机不可达，TCP 三次握手的超时时间由操作系统决定（Windows 默认可能超过 20 秒），会导致驱动在启动阶段长时间阻塞，影响网关启动速度。

**改动点：** 主动使用 `CancellationTokenSource(TimeSpan.FromSeconds(5))` 设置连接超时：

```csharp
private async Task EnsureConnectedAsync(CancellationToken ct)
{
    if (_modbusMaster != null) return;

    _tcpClient = new TcpClient();

    // 用独立 CTS 控制连接超时（默认 5 秒），不阻塞网关启动
    using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, connectCts.Token);

    await _tcpClient.ConnectAsync(_config.Host, _config.Port, linkedCts.Token);

    var factory = new ModbusFactory();
    _modbusMaster = factory.CreateMaster(_tcpClient);

    _logger.LogInformation("[{DriverId}] Modbus 已连接到 {Host}:{Port}", DriverId, _config.Host, _config.Port);
}
```

**同时建议在配置中增加可选字段：**

```csharp
public int ConnectTimeoutMs { get; set; } = 5000;
```

---

### 2. 连接存活状态检测缺失

**现状：** 只用 `_modbusMaster != null` 判断连接是否存在。

```csharp
if (_modbusMaster != null) return;
```

**问题：** 当对端（PLC/仪表）意外断电或网络中断时，TCP 连接不会自动检测到"假死"状态（对端未发送 TCP FIN/RST）。此时 `_modbusMaster` 引用仍存在，但实际连接已失效，`ReadRegistersAsync` 会抛出异常后走重连流程，有一定延迟。

**改动点：** 增加显式的连接状态检查：

```csharp
private bool IsConnected => _tcpClient?.Connected == true && _modbusMaster != null;
```

在 `ReadRegistersAsync` 开始时先检查连接状态，不可用则主动断连并触发重连：

```csharp
if (!IsConnected)
{
    Disconnect();
    return false;
}
```

---

## 中优先级

### 3. `ModbusFactory` 每次连接都 new 实例

**现状：**

```csharp
var factory = new ModbusFactory();
_modbusMaster = factory.CreateMaster(_tcpClient);
```

**问题：** `ModbusFactory` 是无状态工厂，每次 new 浪费内存。改成 `static readonly` 字段复用更高效。

**改动点：**

```csharp
private static readonly ModbusFactory ModbusFactory = new();
```

---

### 4. 固定重连间隔，无退避策略

**现状：** 每次重连间隔固定为 `ReconnectDelayMs`（如 5 秒）。

**问题：** 网络抖动时，频繁重连会产生大量日志和系统开销。断线-重连-又断线形成"震颤"（flapping）。

**改动点：** 实现指数退避：

```csharp
private int _reconnectAttempts = 0;

private int GetReconnectDelay()
{
    // 1s → 2s → 4s → 8s ... 最大 60s
    var delay = Math.Min(_config.ReconnectDelayMs * (1 << _reconnectAttempts), 60_000);
    _reconnectAttempts++;
    return delay;
}

private void OnConnectSuccess() => _reconnectAttempts = 0;
```

在 `EnsureConnectedAsync` 成功后调用 `OnConnectSuccess()`，在 `RunPollingAsync` 的异常处理中调用 `GetReconnectDelay()`。

---

### 5. `RegisterDefinition.Type` 使用字符串，无编译时检查

**现状：**

```csharp
public string Type { get; set; } = "HoldingRegister";
```

**问题：** 配置中拼写错误（如 `"Holdingregister"`）只会在运行时抛 `NotSupportedException`，没有编译期保护。

**改动点：** 改为枚举：

```csharp
public enum RegisterType
{
    HoldingRegister,
    InputRegister,
    Coil,
    DiscreteInput
}

public RegisterType Type { get; set; } = RegisterType.HoldingRegister;
```

同时在 `ReadRegisterValueAsync` 中将 `switch` 改为枚举匹配：

```csharp
return register.Type switch
{
    RegisterType.HoldingRegister => ...,
    RegisterType.InputRegister   => ...,
    RegisterType.Coil            => ...,
    RegisterType.DiscreteInput   => ...,
    _ => throw new NotSupportedException(...)
};
```

**注意：** 这是一个破坏性改动，需要同步更新 `appsettings.json` 和 SQLite 中已持久化的驱动配置。

---

## 低优先级

### 6. `RegisterDefinition.Count` 字段已定义但未实际使用

**现状：** 配置支持 `Count > 1` 读取连续寄存器，但代码只取 `[0]`：

```csharp
// ReadRegisterValueAsync
return register.Type switch
{
    "HoldingRegister" => (await _modbusMaster!.ReadHoldingRegistersAsync(slaveId, address, count))[0],
    ...
};
```

**问题：** 如果用户在配置中设置了 `Count > 1`，后续值被忽略，可能造成误解。

**改动点：** 二选一：
- **选项 A**：在 `ReadRegisterAsync` 中展开为多个 DataPoint（`Temperature[0]`、`Temperature[1]`、...）
- **选项 B**：文档注释明确说明 `Count` 暂未实现，预留接口

---

### 7. 缺少写入（Write）支持

**现状：** 仅实现了轮询读取，未实现 `IWritableDriver` 接口。

**问题：** Modbus 支持 FC05（写单个线圈）、FC06（写单个保持寄存器）、FC15（写多个线圈）、FC16（写多个保持寄存器），用户无法通过网关向 PLC 写入数据。

**改动点：** 实现 `IWritableDriver` 接口：

```csharp
public sealed class ModbusTcpDriver : IProtocolDriver, IWritableDriver
```

增加方法：

```csharp
public async Task<WriteResult> WriteAsync(WriteCommand command, CancellationToken ct = default)
{
    // 根据 command.Address 和 command.Value 调用对应的 FC
}
```

---

### 8. 缺少运行时寄存器热更新

**现状：** `Registers` 列表在 `InitializeAsync` 时绑定，之后不可更改。

**问题：** 运行时需要增删寄存器必须重启驱动实例，影响数据连续性。

**改动点：** 增加 `UpdateRegistersAsync(List<RegisterDefinition> registers)` 方法或在 `DriverContext` 中支持配置热更新。

---

### 9. 缺少采集指标统计

**现状：** 无任何统计信息。

**问题：** 用户无法在监控页面看到驱动采集的成功率、延迟、断连次数等指标。

**改动点：** 增加内部计数器：

```csharp
private long _successCount;
private long _failureCount;
private long _lastLatencyMs;
```

在 `ReadRegistersAsync` 中更新计数，并在 `GetDriverInfo()` 或新增 API 中暴露。

---

### 10. 未使用 `IAsyncDisposable`，但实现了 `ValueTask DisposeAsync()`

**现状：** `IProtocolDriver` 继承自 `IAsyncDisposable`，当前实现了 `ValueTask DisposeAsync()` 但只是同步调用 `Disconnect()`。

**问题：** 如果将来需要异步释放资源（如异步关闭连接池），当前实现会限制扩展性。

**改动点：** 如果确定不需要真正的异步释放，可考虑简化为 `void Dispose()`（实现 `IDisposable`）。否则保持现状并补充注释说明。

---

## 总结

| 优先级 | 编号 | 问题 | 预计改动量 |
|--------|------|------|-----------|
| 高 | 1 | TCP 连接超时 | 小 |
| 高 | 2 | 连接存活检测 | 小 |
| 中 | 3 | ModbusFactory static | 微 |
| 中 | 4 | 指数退避重连 | 中 |
| 中 | 5 | Type 改为枚举 | 中（破坏性） |
| 低 | 6 | Count 未使用 | 小 |
| 低 | 7 | 写入支持 | 大 |
| 低 | 8 | 热更新寄存器 | 中 |
| 低 | 9 | 指标统计 | 中 |
| 低 | 10 | IAsyncDisposable 设计 | 小 |

**推荐执行顺序：** 1 → 2 → 3 → 4 → 5 → 6 → 9 → 7 → 8 → 10
