# Phase 2: 北向数据管道 + MQTT 发布

> 目标：跑通 **南向采集 → 数据管道 → 北向发布** 端到端链路，使网关可用

## 设计决策

| 决策点 | 选择 | 理由 |
|--------|------|------|
| MQTT Broker | 连接外部 Broker (EMQX/Mosquitto) | 工业现场通常有统一 Broker，网关不应重复建设 |
| Payload 格式 | 纯 JSON | 简单调试友好，高频场景优化留到后续 |
| 断线缓冲 | 内存队列（有上限，满了丢弃旧数据） | 与现有 ChannelDataPipeline 策略一致，工业场景宁可丢旧数据也不阻塞采集 |
| 北向扩展性 | IPublisher 接口 + 插件化动态加载 | 后续加 HTTP/WebSocket/Kafka 不改核心 |

## 架构概览

```
南向驱动 (IProtocolDriver)
       │
       ▼ PublishAsync(DataPoint)
  IDataSink (ChannelDataPipeline.Writer)
       │
       ▼ Channel<DataPoint>
  IDataPipeline (消费者循环)
       │
       ▼ DataPoint 分发
  IPublisher (插件化北向输出)
       │
       ├── MqttPublisher  →  外部 MQTT Broker
       ├── (未来) HttpPublisher
       └── (未来) KafkaPublisher
```

## 实现步骤

### Step 1: Abstractions 新增北向接口

**文件**: `AmGateway.Abstractions/IPublisher.cs`

```csharp
namespace AmGateway.Abstractions;

/// <summary>
/// 北向发布器接口 - 所有北向输出插件必须实现
/// 与南向 IProtocolDriver 对称设计
/// </summary>
public interface IPublisher : IAsyncDisposable
{
    /// <summary>
    /// 发布器唯一标识
    /// </summary>
    string PublisherId { get; }

    /// <summary>
    /// 初始化发布器
    /// </summary>
    Task InitializeAsync(PublisherContext context, CancellationToken ct = default);

    /// <summary>
    /// 启动发布器（连接外部系统等）
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 停止发布器（优雅关闭）
    /// </summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// 发布单个数据点
    /// </summary>
    ValueTask PublishAsync(DataPoint point, CancellationToken ct = default);

    /// <summary>
    /// 批量发布数据点
    /// </summary>
    ValueTask PublishBatchAsync(IReadOnlyList<DataPoint> points, CancellationToken ct = default);
}
```

**文件**: `AmGateway.Abstractions/PublisherContext.cs`

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AmGateway.Abstractions;

/// <summary>
/// 发布器上下文 - 在 InitializeAsync 阶段由主机注入
/// 与 DriverContext 对称设计
/// </summary>
public sealed class PublisherContext
{
    public required IConfiguration Configuration { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public required string PublisherInstanceId { get; init; }
}
```

**文件**: `AmGateway.Abstractions/Metadata/PublisherMetadataAttribute.cs`

```csharp
namespace AmGateway.Abstractions.Metadata;

/// <summary>
/// 发布器元数据标记 - 与 DriverMetadataAttribute 对称设计
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PublisherMetadataAttribute : Attribute
{
    public required string Name { get; init; }
    public required string TransportName { get; init; }   // 对应 Driver 的 ProtocolName
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = "";
}
```

### Step 2: PluginHost 扩展支持 Publisher 插件

**修改**: `AmGateway.PluginHost/PluginManager.cs`

- `DiscoverPlugins` 扫描时同时识别 `IProtocolDriver` 和 `IPublisher` 实现类型
- 新增 `CreatePublisherInstance(string transportName)` 方法
- `PluginInfo` 新增 `PublisherType` 和 `PublisherMetadata` 字段（一个 DLL 可以同时包含 Driver 和 Publisher）

**修改**: `AmGateway.PluginHost/PluginInfo.cs`

```csharp
public sealed class PluginInfo
{
    public required string PluginDirectory { get; init; }
    internal PluginLoadContext LoadContext { get; init; } = null!;

    // 南向驱动（可选）
    public Type? DriverType { get; init; }
    public DriverMetadataAttribute? DriverMetadata { get; init; }

    // 北向发布器（可选）
    public Type? PublisherType { get; init; }
    public PublisherMetadataAttribute? PublisherMetadata { get; init; }
}
```

### Step 3: Pipeline 改造 — 消费者分发到 IPublisher

**修改**: `AmGateway/Pipeline/IDataPipeline.cs`

- 新增 `IReadOnlyList<IPublisher> Publishers` 属性，让管道知道向谁分发

**修改**: `AmGateway/Pipeline/ChannelDataPipeline.cs`

- 构造函数接收 `IEnumerable<IPublisher>` (通过 DI)
- `ConsumeLoopAsync` 将 DataPoint 分发给所有已注册的 Publisher
- Publisher 异常不应中断管道，单独 catch + 日志

核心消费循环逻辑：

```csharp
private async Task ConsumeLoopAsync(CancellationToken ct)
{
    await foreach (var point in _channel.Reader.ReadAllAsync(ct))
    {
        foreach (var publisher in _publishers)
        {
            try
            {
                await publisher.PublishAsync(point, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Pipeline] 发布器 {PublisherId} 处理数据点失败: {Tag}",
                    publisher.PublisherId, point.Tag);
            }
        }
    }
}
```

### Step 4: MQTT Publisher 插件实现

**新增项目**: `Plugins/AmGateway.Publisher.Mqtt/AmGateway.Publisher.Mqtt.csproj`

```xml
<Project>
  <!-- 继承 Plugins/Directory.Build.props -->
  <ItemGroup>
    <PackageReference Include="MQTTnet" Version="4.3.7.1207" />
  </ItemGroup>
</Project>
```

**文件**: `Plugins/AmGateway.Publisher.Mqtt/MqttPublisher.cs`

核心逻辑参考旧项目 `PlcGateway/Services/MqttPublishService.cs`，但适配新架构：

- `[PublisherMetadata(Name = "MQTT", TransportName = "mqtt", ...)]`
- 实现 `IPublisher` 接口
- `InitializeAsync`: 从 `PublisherContext.Configuration` 绑定 `MqttPublisherConfig`
- `StartAsync`: 连接 Broker（支持自动重连）
- `PublishAsync`: 将 DataPoint 序列化为 JSON，发布到 topic `{TopicPrefix}/{SourceDriver}/{Tag}`
- `StopAsync`: 断开连接
- 断线缓冲：MQTT 客户端断开时，PublishAsync 静默丢弃（由管道 Channel 层保证不阻塞采集）
- 自动重连：使用 MQTTnet 的 `DisconnectedAsync` 事件触发重连

**Payload 格式**（纯 JSON）：

```json
{
  "tag": "opcua-01/Temperature",
  "value": 23.5,
  "timestamp": "2026-04-15T13:00:00.000Z",
  "quality": "Good",
  "sourceDriver": "opcua-01",
  "metadata": { "nodeId": "ns=2;s=Temperature", "protocol": "opcua" }
}
```

**Topic 规则**: `{TopicPrefix}/{SourceDriver}/{Tag最后一部分}`
- 示例配置 `TopicPrefix = "amgateway"`
- 示例输出: `amgateway/opcua-01/Temperature`

**文件**: `Plugins/AmGateway.Publisher.Mqtt/MqttPublisherConfig.cs`

```csharp
internal sealed class MqttPublisherConfig
{
    public bool Enabled { get; set; } = true;
    public string Broker { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string TopicPrefix { get; set; } = "amgateway";
    public string ClientId { get; set; } = "AmGateway";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int ReconnectDelayMs { get; set; } = 5000;
    public bool UseTls { get; set; } = false;
}
```

### Step 5: appsettings.json 新增 Publishers 配置

```json
{
  "Gateway": {
    "PluginsPath": "plugins",
    "ShutdownTimeoutSeconds": 30
  },
  "Publishers": [
    {
      "Transport": "mqtt",
      "InstanceId": "mqtt-01",
      "Enabled": true,
      "Settings": {
        "Broker": "localhost",
        "Port": 1883,
        "TopicPrefix": "amgateway",
        "ClientId": "AmGateway",
        "ReconnectDelayMs": 5000
      }
    }
  ],
  "Drivers": [ ... ]
}
```

### Step 6: GatewayHostService 集成 Publisher 生命周期

**修改**: `AmGateway/Services/GatewayHostService.cs`

- 构造函数不变，但在 `ExecuteAsync` 中：
  1. 启动数据管道消费者
  2. 发现插件（Driver + Publisher）
  3. 读取 `Publishers` 配置，创建/初始化/启动 Publisher 实例
  4. 读取 `Drivers` 配置，创建/初始化/启动 Driver 实例
- `StopAsync` 中先停 Driver 再停 Publisher，最后停 Pipeline

### Step 7: Program.cs 注册调整

**修改**: `AmGateway/Program.cs`

- `ChannelDataPipeline` 注册时需要拿到 Publisher 列表
- 方案：先创建 Publisher 实例 → 注入到 Pipeline → Pipeline 消费时分发

注册顺序：
```csharp
builder.Services.AddSingleton<PluginManager>();
builder.Services.AddSingleton<IDataPipeline, ChannelDataPipeline>();
builder.Services.AddHostedService<GatewayHostService>();
```

Pipeline 不再通过 DI 获取 Publisher 列表，而是由 GatewayHostService 在启动时通过 `SetPublishers()` 方法注入。

### Step 8: Directory.Build.props 更新

**修改**: `Plugins/Directory.Build.props`

- 无需改动，已支持所有 Plugins 子目录的统一构建和复制

### Step 9: AmGateway.slnx 更新

- 新增 `Plugins/AmGateway.Publisher.Mqtt/AmGateway.Publisher.Mqtt.csproj` 项目

### Step 10: 构建 + 端到端验证

- `dotnet build AmGateway.slnx` 零警告零错误
- 确认 `plugins/AmGateway.Publisher.Mqtt/` 目录生成正确
- 确认配置文件格式正确

## 文件变更清单

| 操作 | 文件 |
|------|------|
| 新增 | `AmGateway.Abstractions/IPublisher.cs` |
| 新增 | `AmGateway.Abstractions/PublisherContext.cs` |
| 新增 | `AmGateway.Abstractions/Metadata/PublisherMetadataAttribute.cs` |
| 修改 | `AmGateway.PluginHost/PluginManager.cs` |
| 修改 | `AmGateway.PluginHost/PluginInfo.cs` |
| 修改 | `AmGateway/Pipeline/IDataPipeline.cs` |
| 修改 | `AmGateway/Pipeline/ChannelDataPipeline.cs` |
| 新增 | `Plugins/AmGateway.Publisher.Mqtt/AmGateway.Publisher.Mqtt.csproj` |
| 新增 | `Plugins/AmGateway.Publisher.Mqtt/MqttPublisher.cs` |
| 新增 | `Plugins/AmGateway.Publisher.Mqtt/MqttPublisherConfig.cs` |
| 修改 | `AmGateway/appsettings.json` |
| 修改 | `AmGateway/Services/GatewayHostService.cs` |
| 修改 | `AmGateway/Program.cs` |
| 修改 | `AmGateway.slnx` |

## 关键设计要点

1. **IPublisher 与 IProtocolDriver 对称**：初始化/启动/停止/释放 生命周期一致
2. **Publisher 插件化**：与 Driver 共享同一套 PluginLoadContext 隔离机制
3. **管道扇出**：一个 DataPoint 分发给所有 Publisher，Publisher 间互不影响
4. **断线策略**：Publisher 断开时数据不阻塞采集（静默丢弃），MQTTnet 自带重连
5. **Topic 扁平化**：`{prefix}/{driverId}/{tagName}` 三级结构，方便 Broker 端通配订阅
