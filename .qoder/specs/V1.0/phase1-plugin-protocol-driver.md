# Phase 1: Plugin-based Protocol Driver Architecture

## Context

AmGateway 是旧项目 PlcGateway 的生产级增强版。旧项目将 Modbus/OPC UA/MQTT 硬编码在一个进程中，无法扩展新协议且存在依赖耦合。本阶段目标是构建**插件式协议驱动架构**，通过 `AssemblyLoadContext` 运行时动态加载驱动插件，实现协议驱动的隔离、可扩展和热插拔基础。首批支持 Modbus TCP、OPC UA、S7、CIP 四种协议。

---

## Architecture Overview

```
AmGateway (Host)
├── GatewayHostService (BackgroundService) ── 管理驱动生命周期
├── ChannelDataPipeline (Channel<DataPoint>) ── 数据管道
└── PluginManager ── 发现/加载/卸载插件
        │
        ├── [ModbusPluginContext]  → ModbusTcpDriver  ──┐
        ├── [OpcUaPluginContext]   → OpcUaDriver      ──┤── DataPoint ──→ Channel
        ├── [S7PluginContext]      → S7Driver          ──┤
        └── [CipPluginContext]     → CipDriver         ──┘

AmGateway.Abstractions (共享契约, 所有 ALC 回退到 Default)
├── IProtocolDriver, IDataSink
├── DataPoint, DataQuality
└── DriverMetadataAttribute, DriverContext
```

---

## Project Structure & Files

```
AmGateway/
├── AmGateway.slnx                              (update)
├── AmGateway.Abstractions/
│   ├── AmGateway.Abstractions.csproj            (new)
│   ├── IProtocolDriver.cs                       (new)
│   ├── IDataSink.cs                             (new)
│   ├── Models/
│   │   ├── DataPoint.cs                         (new)
│   │   └── DataQuality.cs                       (new)
│   ├── Configuration/
│   │   └── DriverConfiguration.cs               (new)
│   └── Metadata/
│       ├── DriverMetadataAttribute.cs            (new)
│       └── DriverContext.cs                      (new)
├── AmGateway.PluginHost/
│   ├── AmGateway.PluginHost.csproj              (new)
│   ├── PluginLoadContext.cs                     (new)
│   ├── PluginInfo.cs                            (new)
│   └── PluginManager.cs                         (new)
├── AmGateway/
│   ├── AmGateway.csproj                         (update: SDK -> Worker, add refs)
│   ├── Program.cs                               (rewrite)
│   ├── Services/
│   │   └── GatewayHostService.cs                (new)
│   ├── Pipeline/
│   │   ├── IDataPipeline.cs                     (new)
│   │   └── ChannelDataPipeline.cs               (new)
│   └── appsettings.json                         (new)
├── Plugins/
│   ├── Directory.Build.props                    (new: 统一插件构建配置)
│   ├── AmGateway.Driver.Modbus/
│   │   ├── AmGateway.Driver.Modbus.csproj       (new)
│   │   └── ModbusTcpDriver.cs                   (new)
│   ├── AmGateway.Driver.OpcUa/
│   │   ├── AmGateway.Driver.OpcUa.csproj        (new)
│   │   └── OpcUaDriver.cs                       (new)
│   ├── AmGateway.Driver.S7/
│   │   ├── AmGateway.Driver.S7.csproj           (new)
│   │   └── S7Driver.cs                          (new)
│   └── AmGateway.Driver.Cip/
│       ├── AmGateway.Driver.Cip.csproj          (new)
│       └── CipDriver.cs                         (new)
└── PlcGateway/                                  (no changes, reference only)
```

---

## Step 1: AmGateway.Abstractions

Zero external dependencies. Pure interfaces and models.

### AmGateway.Abstractions.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

### IProtocolDriver.cs

```csharp
namespace AmGateway.Abstractions;

public interface IProtocolDriver : IAsyncDisposable
{
    string DriverId { get; }
    Task InitializeAsync(DriverContext context, CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
```

### DriverContext.cs

Injected by host during InitializeAsync. "Poor man's DI" since plugins are created via Activator.CreateInstance.

```csharp
namespace AmGateway.Abstractions;

public sealed class DriverContext
{
    public required IConfiguration Configuration { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public required IDataSink DataSink { get; init; }
    public required string DriverInstanceId { get; init; }
}
```

Requires: `Microsoft.Extensions.Configuration.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` (both come from shared framework, no NuGet needed for net10.0).

### IDataSink.cs

```csharp
namespace AmGateway.Abstractions;

public interface IDataSink
{
    ValueTask PublishAsync(DataPoint point, CancellationToken ct = default);
    ValueTask PublishBatchAsync(IReadOnlyList<DataPoint> points, CancellationToken ct = default);
}
```

ValueTask for hot-path perf (Channel writes are usually synchronous completion).

### DataPoint.cs

```csharp
namespace AmGateway.Abstractions.Models;

public readonly record struct DataPoint
{
    public required string Tag { get; init; }
    public required object? Value { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required DataQuality Quality { get; init; }
    public required string SourceDriver { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
```

record struct: value type, low GC pressure for high-throughput scenarios.

### DataQuality.cs

```csharp
namespace AmGateway.Abstractions.Models;

[Flags]
public enum DataQuality : byte
{
    Good         = 0,
    Uncertain    = 1,
    Bad          = 2,
    ConfigError  = 4,
    NotConnected = 8
}
```

### DriverMetadataAttribute.cs

```csharp
namespace AmGateway.Abstractions.Metadata;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class DriverMetadataAttribute : Attribute
{
    public required string Name { get; init; }
    public required string ProtocolName { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = "";
}
```

Read via reflection before instantiation, so PluginManager can log/validate without creating driver instances.

### DriverConfiguration.cs

```csharp
namespace AmGateway.Abstractions.Configuration;

public class DriverConfiguration
{
    public bool Enabled { get; set; } = true;
    public string? InstanceId { get; set; }
}
```

---

## Step 2: AmGateway.PluginHost

### AmGateway.PluginHost.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\AmGateway.Abstractions\AmGateway.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

### PluginLoadContext.cs

Key implementation: shared assembly fallback + native DLL support.

```csharp
class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginDllPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginDllPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared assemblies: fallback to Default (host's version)
        if (IsSharedAssembly(assemblyName.Name))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // For native DLLs like libplctag's plctag.dll
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    private static bool IsSharedAssembly(string? name) =>
        name != null && (
            name == "AmGateway.Abstractions" ||
            name.StartsWith("Microsoft.Extensions.") ||
            name.StartsWith("System.")
        );
}
```

- `isCollectible: true` enables future Unload support
- `AssemblyDependencyResolver` reads the plugin's `.deps.json` to resolve both managed and native paths
- Shared assembly whitelist ensures IProtocolDriver/ILogger/IConfiguration types are identical across host and plugins

### PluginInfo.cs

```csharp
public sealed class PluginInfo
{
    public required string PluginDirectory { get; init; }
    public required PluginLoadContext LoadContext { get; init; }
    public required Type DriverType { get; init; }
    public required DriverMetadataAttribute Metadata { get; init; }
}
```

### PluginManager.cs

Singleton service, responsible for:

1. **DiscoverPlugins(pluginsRootPath)**: Scan `plugins/*/AmGateway.Driver.*.dll`, create PluginLoadContext per plugin, find IProtocolDriver implementations, read DriverMetadataAttribute, store in `Dictionary<string, PluginInfo>` keyed by ProtocolName.

2. **CreateDriverInstance(protocolName)**: `Activator.CreateInstance(pluginInfo.DriverType)` -> cast to `IProtocolDriver`.

3. **UnloadPlugin(protocolName)**: Unload the AssemblyLoadContext.

4. **Error isolation**: Each plugin discovery/load wrapped in try-catch. Single plugin failure logs error, doesn't affect others.

---

## Step 3: AmGateway Host

### AmGateway.csproj (update)

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\AmGateway.Abstractions\AmGateway.Abstractions.csproj" />
    <ProjectReference Include="..\AmGateway.PluginHost\AmGateway.PluginHost.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.5" />
    <PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.1.1" />
    <PackageReference Include="Serilog.Sinks.File" Version="7.0.0" />
  </ItemGroup>
</Project>
```

### Program.cs

Same pattern as old PlcGateway/Program.cs:
- Serilog setup (Console + File `logs/amgateway-.log`)
- `Host.CreateApplicationBuilder(args)`
- `AddWindowsService(options => options.ServiceName = "AmGateway")`
- Register: `PluginManager` (Singleton), `IDataPipeline`/`ChannelDataPipeline` (Singleton), `GatewayHostService` (HostedService)
- Serilog integration

### GatewayHostService.cs

BackgroundService replacing old Worker.cs. No longer hardcodes protocols.

**ExecuteAsync**:
1. Start DataPipeline consumer
2. `PluginManager.DiscoverPlugins(pluginsPath)`
3. Read `Drivers[]` array from config
4. For each driver config where `Enabled=true`:
   - Match `Protocol` field to PluginManager's discovered ProtocolName
   - Build `DriverContext` (config section, LoggerFactory, DataSink, InstanceId)
   - Create instance via PluginManager
   - `await driver.InitializeAsync(context, ct)`
   - `try { await driver.StartAsync(ct) } catch { log, continue }` -- error isolation
   - Add to `_activeDrivers` list
5. `await Task.Delay(Infinite, ct)`

**StopAsync**: Reverse iterate _activeDrivers, StopAsync + DisposeAsync each (with timeout). Then stop pipeline.

### Pipeline/IDataPipeline.cs & ChannelDataPipeline.cs

`Channel.CreateBounded<DataPoint>(10000, DropOldest)` as core data bus.

- **Writer side** (implements IDataSink): `TryWrite(point)` -- non-blocking, drivers never stall
- **Reader side**: Background Task consuming `Reader.ReadAsync(ct)`, Phase 1 just logs DataPoints at Debug level
- **Extension points for later phases**: Insert rule engine between reader and publisher, add persistent queue for offline caching

### appsettings.json

```json
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "Gateway": {
    "PluginsPath": "plugins",
    "ShutdownTimeoutSeconds": 30
  },
  "Drivers": [
    {
      "Protocol": "modbus-tcp",
      "InstanceId": "modbus-01",
      "Enabled": true,
      "Settings": {
        "Host": "127.0.0.1",
        "Port": 502,
        "SlaveId": 1,
        "PollIntervalMs": 1000,
        "Registers": [
          { "Address": 0, "Name": "Temperature", "Type": "HoldingRegister" },
          { "Address": 1, "Name": "Pressure", "Type": "HoldingRegister" }
        ]
      }
    },
    {
      "Protocol": "opcua",
      "InstanceId": "opcua-01",
      "Enabled": true,
      "Settings": {
        "Endpoint": "opc.tcp://localhost:4840",
        "Nodes": [
          { "NodeId": "ns=2;s=Temperature", "Name": "Temperature" }
        ]
      }
    },
    {
      "Protocol": "s7",
      "InstanceId": "s7-01",
      "Enabled": false,
      "Settings": {
        "IpAddress": "192.168.1.30",
        "CpuType": "S71200",
        "Rack": 0,
        "Slot": 0,
        "PollIntervalMs": 500,
        "DataItems": [
          { "Name": "Motor1Speed", "DataType": "Real", "DbNumber": 1, "StartByte": 0 }
        ]
      }
    },
    {
      "Protocol": "cip",
      "InstanceId": "cip-01",
      "Enabled": false,
      "Settings": {
        "Gateway": "192.168.1.40",
        "Path": "1,0",
        "PlcType": "ControlLogix",
        "Tags": [
          { "Name": "ConveyorSpeed", "TagName": "Conveyor_Speed", "DataType": "REAL" }
        ]
      }
    }
  ]
}
```

Key design: `Drivers` is an array (same protocol can have multiple instances). `Settings` is opaque JSON passed as IConfiguration to the driver -- host never parses it.

---

## Step 4: Protocol Driver Plugins

### Plugins/Directory.Build.props

Placed in `Plugins/` directory, inherited by all driver projects:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\AmGateway.Abstractions\AmGateway.Abstractions.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

- `EnableDynamicLoading=true`: Generates correct `.deps.json`, prevents copying shared framework assemblies
- Abstractions ref with `Private=false` + `ExcludeAssets=runtime`: Compile-time only, not copied to output (ALC fallback handles it)

Each driver csproj only needs its protocol-specific NuGet packages.

### Plugin build output

MSBuild Target in `Plugins/Directory.Build.props` to copy plugin output to `plugins/{name}/`:

```xml
<Target Name="CopyPluginToHost" AfterTargets="Build">
  <PropertyGroup>
    <PluginTargetDir>$(SolutionDir)AmGateway\bin\$(Configuration)\net10.0\plugins\$(AssemblyName)\</PluginTargetDir>
  </PropertyGroup>
  <ItemGroup>
    <PluginFiles Include="$(OutputPath)**\*.*" />
  </ItemGroup>
  <Copy SourceFiles="@(PluginFiles)"
        DestinationFiles="@(PluginFiles->'$(PluginTargetDir)%(RecursiveDir)%(Filename)%(Extension)')"
        SkipUnchangedFiles="true" />
</Target>
```

Runtime directory layout:
```
bin/Debug/net10.0/
├── AmGateway.exe
├── AmGateway.Abstractions.dll
├── plugins/
│   ├── AmGateway.Driver.Modbus/
│   │   ├── AmGateway.Driver.Modbus.dll + .deps.json
│   │   └── NModbus.dll
│   ├── AmGateway.Driver.OpcUa/
│   │   ├── AmGateway.Driver.OpcUa.dll + .deps.json
│   │   └── Opc.Ua.*.dll
│   ├── AmGateway.Driver.S7/
│   │   └── S7netplus.dll
│   └── AmGateway.Driver.Cip/
│       ├── libplctag.dll (managed)
│       └── runtimes/win-x64/native/plctag.dll
```

### 4.1 AmGateway.Driver.Modbus

**csproj**: `NModbus` 3.0.81

**ModbusTcpDriver.cs**: Implements `IProtocolDriver`, marked with `[DriverMetadata(Name="Modbus TCP", ProtocolName="modbus-tcp")]`

Core logic reused from `PlcGateway/Services/ModbusClientService.cs`:
- `EnsureConnectedAsync`: TcpClient + `ModbusFactory().CreateMaster()` -- identical to old code
- Polling loop: `while + PollIntervalMs delay` -- same pattern as old `RunPollingAsync`
- Register read: `ReadHoldingRegistersAsync / ReadInputRegistersAsync` -- same switch dispatch
- **Adaptation**: Instead of `_mqttPublish.PublishAsync()`, construct `DataPoint` and call `_dataSink.PublishAsync()`
- Reconnect: Disconnect + configurable delay (default 5s) -- same as old code

Internal config: `ModbusDriverConfig` { Host, Port, SlaveId, PollIntervalMs, ReconnectDelayMs, Registers[] }

### 4.2 AmGateway.Driver.OpcUa

**csproj**: `OPCFoundation.NetStandard.Opc.Ua` / `.Client` / `.Core` 1.5.378.134

**OpcUaDriver.cs**: Implements `IProtocolDriver`, marked with `[DriverMetadata(Name="OPC UA", ProtocolName="opcua")]`

Core logic reused from `PlcGateway/Services/OpcUaClientService.cs`:
- `ConnectAsync`: ApplicationConfiguration + DiscoveryClient + Session.Create -- identical
- `CreateSubscription`: Subscription + MonitoredItem + Notification callback -- same structure
- **Adaptation**: Callback constructs DataPoint (use OPC UA SourceTimestamp as DataPoint.Timestamp, map StatusCode to DataQuality)

Internal config: `OpcUaDriverConfig` { Endpoint, SecurityPolicy, PublishingIntervalMs, SamplingIntervalMs, Nodes[] }

### 4.3 AmGateway.Driver.S7

**csproj**: `S7netplus` 0.20.0

**S7Driver.cs**: New implementation, polling pattern same as Modbus.

- Connect: `new Plc(cpuType, ip, rack, slot)` -> `plc.OpenAsync()`
- Read: `plc.ReadAsync(DataType, DbNumber, StartByte, Count)` per configured data item
- Parse value based on DataType config (Bool, Byte, Int, DInt, Real, DWord)
- Reconnect on `PlcException`

Internal config: `S7DriverConfig` { IpAddress, CpuType, Rack, Slot, PollIntervalMs, DataItems[] }

### 4.4 AmGateway.Driver.Cip

**csproj**: `libplctag` 1.5.2

**CipDriver.cs**: New implementation, polling pattern. **Native DLL handled by PluginLoadContext.LoadUnmanagedDll**.

- Init: Create `Tag` objects per config, set Protocol/Gateway/Path/PlcType
- Read: `tag.ReadAsync()` -> `tag.GetInt32(0)` / `tag.GetFloat32(0)` based on DataType
- Dispose: Destroy all Tag handles

Internal config: `CipDriverConfig` { Gateway, Path, PlcType, PollIntervalMs, Tags[] }

---

## Step 5: Solution File Update

```xml
<Solution>
  <Project Path="AmGateway/AmGateway.csproj" />
  <Project Path="AmGateway.Abstractions/AmGateway.Abstractions.csproj" />
  <Project Path="AmGateway.PluginHost/AmGateway.PluginHost.csproj" />
  <Folder Name="Plugins">
    <Project Path="Plugins/AmGateway.Driver.Modbus/AmGateway.Driver.Modbus.csproj" />
    <Project Path="Plugins/AmGateway.Driver.OpcUa/AmGateway.Driver.OpcUa.csproj" />
    <Project Path="Plugins/AmGateway.Driver.S7/AmGateway.Driver.S7.csproj" />
    <Project Path="Plugins/AmGateway.Driver.Cip/AmGateway.Driver.Cip.csproj" />
  </Folder>
</Solution>
```

PlcGateway not included in solution -- kept in repo as reference only.

---

## Implementation Order

```
Step 1: AmGateway.Abstractions     (interfaces + models, zero deps)
Step 2: AmGateway.PluginHost       (PluginLoadContext + PluginManager)
Step 3: AmGateway host             (Program.cs + GatewayHostService + Pipeline)
Step 4: Plugins/Directory.Build.props (unified plugin build config)
Step 5: AmGateway.Driver.Modbus    (first driver, has old code reference)
Step 6: AmGateway.Driver.OpcUa     (second, has old code reference)
Step 7: AmGateway.Driver.S7        (third, new but simple API)
Step 8: AmGateway.Driver.Cip       (last, native DLL complexity)
Step 9: AmGateway.slnx update
Step 10: Build + verify
```

---

## Package Version Reference

| Package | Version | Source |
|---------|---------|--------|
| NModbus | 3.0.81 | Old project |
| OPCFoundation.NetStandard.Opc.Ua | 1.5.378.134 | Old project |
| OPCFoundation.NetStandard.Opc.Ua.Client | 1.5.378.134 | Old project |
| OPCFoundation.NetStandard.Opc.Ua.Core | 1.5.378.134 | Old project |
| MQTTnet | 5.1.0.1559 | Old project (not used in Phase 1, reserved for Phase 2) |
| Serilog.AspNetCore | 10.0.0 | Old project |
| Serilog.Sinks.Console | 6.1.1 | Old project |
| Serilog.Sinks.File | 7.0.0 | Old project |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.5 | Old project |
| S7netplus | 0.20.0 | New |
| libplctag | 1.5.2 | New |

---

## Verification

1. **Build**: `dotnet build AmGateway.slnx` -- all projects compile with zero errors
2. **Plugin output**: Verify `AmGateway/bin/Debug/net10.0/plugins/` contains 4 subdirectories with correct DLLs and .deps.json files
3. **Runtime startup**: `dotnet run --project AmGateway/AmGateway.csproj` -- verify logs show:
   - Plugin discovery: "Discovered plugin: Modbus TCP (modbus-tcp)"
   - Driver initialization per enabled config entry
   - DataPoint flowing through Channel (Debug level log)
4. **Error isolation**: Set an invalid host in one driver config, verify other drivers still start successfully
5. **ALC isolation**: Verify no `InvalidCastException` or type mismatch errors (shared assembly fallback working)
