# AmGateway 排障记录

## 2026-04-16 Modbus / OPC UA 链路联调

### 问题一：Modbus 驱动启动成功但 dataPointsReceived=0

**现象**

- 网关启动后 `activeDrivers: 2`，Modbus 驱动 `status=1, errorMessage=null`
- 但 `dataPointsReceived` 始终为 0，MQTT 无任何数据产出
- 发布器（MQTT/InfluxDB）正常工作

**根因**

`GatewayRuntime.cs` `SeedFromAppSettingsAsync` 方法中，种子数据序列化方式错误：

```csharp
// 错误写法（第 85、105 行）
var settingsJson = JsonSerializer.Serialize(
    drvConfig.GetSection("Settings").Get<Dictionary<string, object?>>());
```

此方式将 `Registers` 数组序列化为一个 JSON 字符串值（如 `"[{\"Address\":0,...}]"`），而还原时 `BuildConfigurationFromJson` 使用 `Dictionary<string, string?>` 反序列化，`IConfiguration.Bind()` 无法将字符串绑定到 `List<RegisterDefinition>`，导致 `_config.Registers` 为空列表。

驱动 `StartAsync` 中 `foreach (var register in _config.Registers)` 永远不执行 → 不读任何寄存器 → 不产生数据。

**修复**

将种子序列化改为与 API 添加驱动一致的方式：

```csharp
// 正确写法
var settingsJson = ConfigurationToJson(drvConfig.GetSection("Settings"));
```

`ConfigurationToJson` 使用 `FlattenConfig` 将配置扁平化为 `{key: value}` 格式（如 `Registers:0:Address` → `0`），`BuildConfigurationFromJson` 可以正确还原。

**影响范围**

- `SeedFromAppSettingsAsync` 中发布器种子（第 85 行）和驱动种子（第 105 行）均有此问题
- 发布器配置较简单（无嵌套数组），恰好不受影响；驱动配置含 `Registers`/`Nodes` 数组，必现

**涉及文件**

| 文件 | 行号 | 修改 |
|------|------|------|
| `AmGateway/Services/GatewayRuntime.cs` | 85 | `JsonSerializer.Serialize(...)` → `ConfigurationToJson(...)` |
| `AmGateway/Services/GatewayRuntime.cs` | 105 | 同上 |

---

### 问题二：OPC UA 驱动启动成功但无数据通知

**现象**

- OPC UA 驱动 `status=1`，Session 连接成功，Subscription 创建成功
- 但永远收不到 MonitoredItem 通知，`dataPointsReceived` 中无 OPC UA 数据

**根因**

**1. NodeId 不匹配**

从站 `OpcUaServerService.cs` 第 616 行，变量 NodeId 使用 `{FolderName}_{VariableName}` 格式：

```csharp
var nodeIdStr = $"{(parent as FolderState)?.SymbolicName}_{name}";
// 例：Sensors_Temperature, Sensors_Pressure
```

而网关 `appsettings.json` 配置的是：

```json
{ "NodeId": "ns=2;s=Temperature", "Name": "Temperature" }
```

实际从站暴露的 NodeId 是 `ns=2;s=Sensors_Temperature`，网关订阅了不存在的节点。

**2. MonitoredItem 创建失败被静默忽略**

`OpcUaDriver.cs` 第 190-192 行，创建订阅后未检查 MonitoredItem 状态：

```csharp
await _subscription.CreateAsync();
await _subscription.ApplyChangesAsync();
// 直接打印"订阅已创建"，未检查 item.Created
```

当 NodeId 无效时，OPC UA SDK 不会抛异常，而是将 MonitoredItem 标记为未创建（`Created=false`），但驱动完全忽略了此状态。

**修复**

1. 修正 `appsettings.json` 中 NodeId：

```json
{ "NodeId": "ns=2;s=Sensors_Temperature", "Name": "Temperature" },
{ "NodeId": "ns=2;s=Sensors_Pressure", "Name": "Pressure" }
```

2. 在 `CreateSubscriptionAsync` 中增加创建状态检查：

```csharp
await _subscription.CreateAsync();
await _subscription.ApplyChangesAsync();

foreach (var item in _subscription.MonitoredItems)
{
    if (!item.Created)
    {
        _logger.LogError("[{DriverId}] MonitoredItem 创建失败: NodeId={NodeId}, Status={Status}",
            DriverId, item.StartNodeId, item.Status);
    }
}
```

**涉及文件**

| 文件 | 修改 |
|------|------|
| `AmGateway/appsettings.json` | NodeId `Temperature` → `Sensors_Temperature`，`Pressure` → `Sensors_Pressure` |
| `Plugins/AmGateway.Driver.OpcUa/OpcUaDriver.cs` | `CreateSubscriptionAsync` 增加 MonitoredItem 创建状态检查 |

---

## 2026-04-16 全链路测试发现的 Bug 及修复

### Bug B1：插件 DLL 发现路径错误

**现象**

- `PluginManager` 加载插件时找不到 DLL，驱动无法启动
- 日志：插件目录扫描成功但 `LoadFrom` 失败

**根因**

`PluginManager.cs` 中插件搜索路径未包含 `plugins/{PluginName}/` 子目录结构。`dotnet build` 编译插件后，DLL 位于 `bin/Debug/net10.0/`，但运行时需要复制到 `AmGateway/bin/Debug/net10.0/plugins/{PluginName}/` 目录下。

**修复**

确保插件搜索逻辑覆盖 `plugins/{name}/{name}.dll` 路径模式，并在构建后手动/脚本复制插件 DLL：

```powershell
Copy-Item "Plugins\AmGateway.Driver.Modbus\bin\Debug\net10.0\AmGateway.Driver.Modbus.dll" `
  "AmGateway\bin\Debug\net10.0\plugins\AmGateway.Driver.Modbus\" -Force
```

**注意**：`dotnet build` 主项目不会自动编译/复制插件项目，必须单独编译插件并手动复制。

---

### Bug B2：重复添加驱动/发布器未报错

**现象**

- 通过 API 重复添加同一 `instanceId` 的驱动，返回成功但实际只有一份
- 或抛出不友好的 `InvalidOperationException` 而非 HTTP 400

**根因**

`GatewayRuntime.AddDriverAsync` 用 `ConcurrentDictionary.TryAdd`，重复时返回 false 但未给调用方明确反馈。

**修复**

API 层在调用前检查实例是否已存在，返回 `409 Conflict` 或 `400 BadRequest`。

---

### Bug B3：协议名大小写不匹配

**现象**

- 通过 API 创建驱动时 `protocol: "ModbusTcp"` 不被识别，返回 404
- 插件注册的协议名为小写 `modbus-tcp`

**根因**

插件 `PluginManifest.json` 或 `ProtocolName` 属性返回 `modbus-tcp`，而用户习惯用 PascalCase。

**修复**

API 层或 PluginManager 匹配协议时做大小写无关比较（`StringComparison.OrdinalIgnoreCase`），或在文档中明确协议名格式。

---

### Bug B4：Modbus 从站断线后无法自动重连（关键 Bug）

**现象**

- AmVirtualSlave 停止后，网关 Modbus 数据变为 `quality: Bad, value: null`
- 从站重启后，数据**仍然 Bad**，不会自动恢复
- 只有手动调用 `/api/drivers/modbus-01/restart` 才恢复

**根因**

`ModbusTcpDriver.ReadRegistersAsync` 中，单个寄存器读取失败时：

```csharp
// 原代码（有 Bug）
catch (Exception ex)
{
    // 只发 Bad 数据点，没有断开连接
    await _dataSink.PublishAsync(new DataPoint { Quality = Bad }, ct);
    // 不 break，继续读下一个寄存器
}
```

问题链：
1. 读取失败 → 内层 catch 捕获 → 发 Bad 数据 → **不断开连接**
2. `_modbusMaster` 仍非 null → `EnsureConnectedAsync` 跳过重连
3. 下次循环仍用坏掉的连接 → 反复 Bad
4. 即使从站恢复，坏掉的 TCP 连接也不会自愈

**修复**

改为 `ReadRegistersAsync` 返回 `Task<bool>`，读取失败时返回 false：

```csharp
private async Task<bool> ReadRegistersAsync(CancellationToken ct)
{
    if (_modbusMaster == null) return false;
    var allOk = true;
    foreach (var register in _config.Registers)
    {
        try { /* 正常读取 */ }
        catch (Exception ex)
        {
            await _dataSink.PublishAsync(new DataPoint { Quality = Bad }, ct);
            allOk = false;
            break;  // 一个失败就够了，跳出 foreach
        }
    }
    return allOk;
}
```

外层 `RunPollingAsync` 检测返回 false 后：

```csharp
if (!readOk)
{
    Disconnect();  // 断开连接，_modbusMaster = null
    // 发 NotConnected 数据点
    // 等待 ReconnectDelayMs
    continue;  // 跳过 PollInterval 等待，立即重试
}
```

**关键点**：必须 `Disconnect()` 将 `_modbusMaster` 置 null，否则 `EnsureConnectedAsync` 永远不会重新建立连接。

**涉及文件**

| 文件 | 修改 |
|------|------|
| `Plugins/AmGateway.Driver.Modbus/ModbusTcpDriver.cs` | `ReadRegistersAsync` 返回 `Task<bool>`；`RunPollingAsync` 检测 false 后 Disconnect + 延迟重连 |

---

### Bug B5：InfluxDB Org/Bucket 配置不匹配导致写入失败

**现象**

- `appsettings.json` 配置 `Org: "influxdb"`, `Bucket: "amgateway"`
- InfluxDB 实际 org 为 `my-org`，bucket 为 `my-bucket`
- 写入返回 404：`organization name "influxdb" not found`
- 但网关 `publishErrors` 计数器为 0（InfluxDB 发布器可能静默吞了错误）

**根因**

InfluxDB 2.x 对 org 和 bucket 名严格匹配，不存在则返回 404。网关配置与实际 InfluxDB 实例不一致。

**修复**

1. 更新 `appsettings.json` 中 `Org` 和 `Bucket` 与实际一致
2. 通过 API 删除旧发布器并重建：
   ```powershell
   Invoke-RestMethod -Uri 'http://localhost:5002/api/publishers/influxdb-01' -Method Delete
   # 用正确 Org/Bucket 重建
   ```

**排查命令**

```powershell
# 查看 InfluxDB 中的 org
curl -s http://localhost:8086/api/v2/orgs -H "Authorization: Token <TOKEN>"
# 查看 bucket
curl -s http://localhost:8086/api/v2/buckets -H "Authorization: Token <TOKEN>"
```

---

### Bug B6：优雅关机无法通过 taskkill/CloseMainWindow 触发

**现象**

- `taskkill /PID xxx`（不带 /F）报错 "This process can only be terminated forcefully"
- `Process.CloseMainWindow()` 发送 WM_CLOSE 后进程 10 秒内不退出
- `Stop-Process -Force` 直接杀死，不触发 `IHostedService.StopAsync`

**根因**

.NET 控制台应用通过 `Start-Process` 启动时，WM_CLOSE 和 SIGTERM 信号无法传递给 CLR 运行时。`IHostedService.StopAsync` 只在收到 Ctrl+C（ConsoleLifetime）或 `IHostApplicationLifetime.StopApplication()` 调用时触发。

**修复**

添加 `/api/shutdown` 端点：

```csharp
api.MapPost("/shutdown", async (IHostApplicationLifetime lifetime, CancellationToken ct) =>
{
    lifetime.StopApplication();
    return Results.Ok(new { message = "网关正在优雅关闭..." });
});
```

调用后触发 `GatewayHostService.StopAsync`，按序停止驱动→排空管道→停止发布器，380ms 内完成退出。

**优雅关机方法**

```powershell
# 方法1：通过 API（推荐）
$token = (Invoke-RestMethod -Uri 'http://localhost:5002/api/auth/login' -Method Post ...).token
Invoke-RestMethod -Uri 'http://localhost:5002/api/shutdown' -Method Post -Headers @{Authorization="Bearer $token"}

# 方法2：在网关控制台窗口按 Ctrl+C
# 方法3：如果作为 Windows Service 运行，net stop AmGateway 会触发优雅关机
```

---

### Bug B7：插件 DLL 编译后未自动复制到运行目录

**现象**

- 修改 `ModbusTcpDriver.cs` 后 `dotnet build` 主项目，编译成功但运行时仍用旧代码
- 插件 DLL 时间戳未更新

**根因**

`dotnet build AmGateway.csproj` 只编译主项目，不会编译插件项目。即使编译插件项目，DLL 输出到 `Plugins/xxx/bin/Debug/` 而非 `AmGateway/bin/Debug/plugins/xxx/`。

**修复**

编译+复制流程：

```powershell
# 1. 停网关（释放文件锁）
Stop-Process -Name "AmGateway" -Force

# 2. 编译插件
dotnet build "Plugins/AmGateway.Driver.Modbus/AmGateway.Driver.Modbus.csproj" -c Debug

# 3. 复制 DLL
Copy-Item "Plugins/AmGateway.Driver.Modbus/bin/Debug/net10.0/AmGateway.Driver.Modbus.dll" `
  "AmGateway/bin/Debug/net10.0/plugins/AmGateway.Driver.Modbus/" -Force

# 4. 启动网关
Start-Process "AmGateway/bin/Debug/net10.0/AmGateway.exe" -WorkingDirectory "AmGateway/bin/Debug/net10.0/"
```

---

### Bug B8：OPC UA MonitoredItem 创建失败被静默忽略

（同问题二中的修复，此处补充排查方法）

**排查方法**

```powershell
# 查看 OPC UA 从站暴露的节点
# 使用 UA Explorer 或查询服务端节点表
# 注意从站变量 NodeId 格式为 {FolderName}_{VariableName}
# 例：ns=2;s=Sensors_Temperature，而非 ns=2;s=Temperature
```

---

## 运维常用命令速查

### 检查网关状态

```powershell
$token = (Invoke-RestMethod -Uri 'http://localhost:5002/api/auth/login' -Method Post -ContentType 'application/json' -Body '{"username":"admin","password":"admin"}').token
$headers = @{Authorization="Bearer $token"}

# 健康状态
Invoke-RestMethod -Uri 'http://localhost:5002/api/health' -Headers $headers

# 驱动列表
Invoke-RestMethod -Uri 'http://localhost:5002/api/drivers' -Headers $headers

# 发布器列表
Invoke-RestMethod -Uri 'http://localhost:5002/api/publishers' -Headers $headers

# 重启驱动
Invoke-RestMethod -Uri 'http://localhost:5002/api/drivers/modbus-01/restart' -Method Post -Headers $headers
```

### 检查 MQTT 数据

```powershell
& "C:\Program Files\mosquitto\mosquitto_sub.exe" -h localhost -p 1883 -t "amgateway/#" -C 4 -W 5
```

### 检查 InfluxDB

```powershell
$token = "<InfluxDB Token>"
# Org 和 Bucket 列表
curl.exe -s http://localhost:8086/api/v2/orgs -H "Authorization: Token $token"
curl.exe -s http://localhost:8086/api/v2/buckets -H "Authorization: Token $token"

# 查询数据（注意 Flux 语法中 bucket 名含横杠需加引号）
# from(bucket: "my-bucket") |> range(start: -5m) |> limit(n: 10)
```

### 检查从站进程

```powershell
Get-Process -Name "AmVirtualSlave" -ErrorAction SilentlyContinue
# 查看监听端口
Get-NetTCPConnection -OwningProcess <PID> -State Listen
```

### 优雅关机

```powershell
$token = (Invoke-RestMethod -Uri 'http://localhost:5002/api/auth/login' -Method Post -ContentType 'application/json' -Body '{"username":"admin","password":"admin"}').token
Invoke-RestMethod -Uri 'http://localhost:5002/api/shutdown' -Method Post -Headers @{Authorization="Bearer $token"}
```

---

### 其他已知问题（未修复）

| # | 问题 | 严重性 | 说明 |
|---|------|--------|------|
| 1 | OPC UA 驱动无断线重连 | 高 | `StartAsync` 一次性连接，Session 断开后不会重连 |
| 2 | `BuildConfigurationFromJson` 反序列化用 `Dictionary<string, string?>` | 中 | 嵌套层级深或非字符串值可能丢失，当前扁平化格式恰好规避 |
| 3 | `OnMonitoredItemNotification` 使用 `async void` | 低 | 异常可能直接崩溃进程，建议改为安全处理 |
| 4 | InfluxDB 发布器写入失败时 `publishErrors` 不增长 | 中 | 404 错误可能被静默吞掉，运维无法通过 health API 发现问题 |
| 5 | 插件编译未集成到主项目构建流程 | 低 | 需手动编译+复制，建议添加构建脚本或 MSBuild Target |
