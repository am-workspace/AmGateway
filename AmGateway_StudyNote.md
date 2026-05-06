# AmGateway 学习笔记

## 一、System.Text.Json 序列化配置

ASP.NET Core 内置 `System.Text.Json` 作为默认 JSON 序列化器，无需额外安装 NuGet 包。

```csharp
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
```

### 两个配置项的作用

| 配置 | 作用 | 示例 |
|------|------|------|
| `JsonStringEnumConverter(CamelCase)` | 枚举序列化为字符串（camelCase），而非默认整数 | `Gender.Male` → `"male"` 而非 `0` |
| `JsonIgnoreCondition.WhenWritingNull` | 属性值为 null 时自动忽略，不输出到 JSON | 不输出 `"field": null` |

### 效果对比

```json
// 配置前
{ "status": 0, "error": null }

// 配置后
{ "status": "pending" }
```

### 相关命名空间

- `System.Text.Json.Serialization` — `JsonStringEnumConverter`、`JsonIgnoreCondition`
- `System.Text.Json` — `JsonNamingPolicy`

### 与 Newtonsoft.Json 对比

| | System.Text.Json | Newtonsoft.Json |
|---|---|---|
| 来源 | 微软内置 | 第三方 NuGet 包 |
| 性能 | 更高、内存分配更少 | 略低 |
| 功能 | 满足大多数场景，高级功能略少 | 功能最全 |

---

## 二、Interlocked 与 ConcurrentQueue 线程安全机制

### Interlocked

`System.Threading.Interlocked` — 提供原子操作，让简单数值运算在多线程下安全，无需加锁。

| 方法 | 作用 |
|------|------|
| `Increment(ref long)` | 原子 +1 |
| `Decrement(ref long)` | 原子 -1 |
| `Add(ref long, long)` | 原子加指定值 |
| `Exchange(ref int, int)` | 原子赋值（返回旧值） |
| `CompareExchange(ref int, int, int)` | CAS，相等才赋值 |
| `Read(ref long)` | 原子读取 64 位值 |

**GatewayMetrics 中的用法：**

```csharp
Interlocked.Increment(ref _dataPointsReceived);  // 计数器 +1，线程安全
Interlocked.Exchange(ref _channelCount, count);   // 赋值，线程安全
Interlocked.Read(ref _dataPointsReceived);        // 原子读取，避免读到写了一半的值
```

**优点：** 比 `lock` 轻量得多，是 CPU 级别的原子指令，没有上下文切换开销。

---

### ConcurrentQueue\<T\>

`System.Collections.Concurrent.ConcurrentQueue<T>` — 线程安全的 FIFO 队列，多线程生产/消费无需加锁。

| 方法 | 作用 |
|------|------|
| `Enqueue(T)` | 入队（线程安全） |
| `TryDequeue(out T)` | 尝试出队，空返回 false |
| `TryPeek(out T)` | 查看队首不出队 |
| `ToArray()` | 快照，返回当前队列的副本 |
| `Count` | 当前元素数量 |
| `IsEmpty` | 是否为空 |

**GatewayMetrics 中的用法：**

```csharp
_recentLatencyMs.Enqueue(elapsed.TotalMilliseconds);  // 多线程记录延迟
while (_recentLatencyMs.Count > MaxLatencySamples)
    _recentLatencyMs.TryDequeue(out _);               // 超出上限则淘汰最旧的
```

**内部原理：** 使用无锁算法（CAS 链表），多个线程可以同时写入、读取，只有对同一槽位的竞争才需要重试。

---

### 注意事项

1. **`Count` 属性较慢** — 需要遍历内部段来计算，高频调用不推荐
2. **不保证强一致性快照** — `ToArray()` 是近似快照，遍历期间可能有新元素入队
3. **复合操作非原子** — "先检查再出队"这类组合操作仍不安全

### 三种同步方式对比

| | `Interlocked` | `ConcurrentQueue` | `lock` |
|---|---|---|---|
| 适用场景 | 单个数值的原子操作 | 多线程生产/消费集合 | 任意复杂临界区 |
| 性能 | 最高 | 高 | 最低（有锁竞争） |
| 灵活性 | 最低（只能操作数值） | 中（队列操作） | 最高（任意代码） |

**GatewayMetrics 的设计思路：能用 `Interlocked` 就不用 `lock`，能用无锁集合就不用锁，保证高吞吐量下的低开销。**

---

## 三、延迟采样机制

### 概述

`GatewayMetrics` 通过滑动窗口记录最近 1000 条数据点的处理耗时，用于计算 P50/P95/P99 等延迟分位数。

### 调用方

`ChannelDataPipeline.cs`，在 Pipeline 消费循环中：

```csharp
await foreach (var point in _channel.Reader.ReadAllAsync(ct))
{
    var sw = Stopwatch.StartNew();          // 开始计时

    // 1. 转换阶段（Transform）
    // 2. 快照读取发布器列表
    // 3. 路由阶段（Resolve）
    // 4. 扇出发布（PublishAsync）

    sw.Stop();                              // 停止计时
    _metrics.RecordLatency(sw.Elapsed);     // 记录耗时
}
```

### 监控范围

单个数据点从 Channel 被消费，经过**转换 → 路由 → 发布**全流程的总耗时（毫秒）。

### 数据来源

- 存入的是 `sw.Elapsed.TotalMilliseconds`，即一个 `double` 类型的纯耗时值
- 不存时间戳、不存数据点内容、不存来源信息

### 滑动窗口原理

队列固定容量 1000，超出时淘汰最旧记录，始终只保留最近 1000 条：

```
[1, 2, 3, ... 1000]           ← 满 1000 条
[2, 3, ... 1000, 1001]        ← 淘汰最旧的 1，加入 1001
[3, ... 1000, 1001, 1002]     ← 淘汰最旧的 2，加入 1002
```

### 分位数计算

导出时 `ToArray()` 取快照 → 排序 → 按百分位取值，如 P95 = 15ms 表示 95% 的数据点在 15ms 内处理完成。

---

## 四、项目启动与加载顺序

### 阶段一：Program.cs — 构建 DI 容器

| 步骤 | 位置 | 做了什么 |
|------|------|---------|
| 1 | `Program.cs:9-15` | 初始化 Serilog 日志（Console + 文件滚动） |
| 2 | `Program.cs:21` | 创建 `WebApplicationBuilder` |
| 3 | `Program.cs:24-28` | 配置 JSON 序列化（枚举字符串、null 忽略） |
| 4 | `Program.cs:30-33` | 注册 Windows 服务支持 |
| 5 | `Program.cs:36-48` | 注册核心 Singleton 服务 |

DI 注册顺序：

```
GatewayMetrics          ← 指标采集器
PluginManager           ← 插件管理器
IDataPipeline           ← ChannelDataPipeline（数据管道）
ITransformEngine        ← TransformEngine（转换引擎）
IRouteResolver          ← RouteResolver（路由解析器）
IConfigRepository       ← SqliteConfigRepository（SQLite 持久化）
GatewayRuntime          ← 运行时管理器
GatewayHostService      ← 后台主服务（BackgroundService）
```

### 阶段二：Program.cs — 构建应用

| 步骤 | 位置 | 做了什么 |
|------|------|---------|
| 6 | `Program.cs:57` | `builder.Build()` — 解析所有 DI，创建实例 |
| 7 | `Program.cs:60` | 开发环境异常页 |
| 8 | `Program.cs:63` | 设置全局 `GatewayRuntimeAccessor.Runtime` |
| 9 | `Program.cs:66` | JWT 认证中间件 + 登录端点 |
| 10 | `Program.cs:69` | 注册业务 API 端点 |

### 阶段三：GatewayHostService.ExecuteAsync — 核心启动逻辑

`app.Run()` 触发 `GatewayHostService`（BackgroundService），按以下顺序执行：

```
步骤1  PluginManager.DiscoverPlugins(pluginsPath)
       └─ 扫描 plugins/ 目录下所有子目录
       └─ 加载 AmGateway.*.dll
       └─ 反射查找 IProtocolDriver / IPublisher 实现
       └─ 注册到内部字典（此时不创建实例）

步骤2  获取 GatewayRuntime（从全局访问器）

步骤3  runtime.SeedFromAppSettingsAsync()
       └─ 检查 SQLite 是否已有数据
       └─ 若为空：将 appsettings.json 中的 Drivers/Publishers 种子到 SQLite
       └─ 若非空：跳过（后续统一从持久化加载）

步骤4  runtime.StartPublishersFromPersistenceAsync()
       └─ 从 SQLite 读取所有 Publisher 配置
       └─ 跳过 Enabled=false 的
       └─ PluginManager.CreatePublisherInstance() 创建实例
       └─ InitializeAsync() → StartAsync() 启动每个发布器

步骤5  pipeline.SetPublishers(publisherInstances)
       └─ 将启动好的发布器列表注入 Pipeline

步骤6  pipeline.SetTransformEngine() / SetRouteResolver()
       └─ 注入转换引擎和路由解析器

步骤7  runtime.LoadTransformRulesFromPersistenceAsync()
       └─ 从 SQLite 加载转换规则 → TransformEngine.LoadRules()

步骤8  runtime.LoadRouteRulesFromPersistenceAsync()
       └─ 从 SQLite 加载路由规则 → RouteResolver.LoadRules()

步骤9  pipeline.StartConsumingAsync()
       └─ 启动 Channel 消费循环
       └─ 数据点开始流转：Transform → Route → Publish

步骤10 runtime.StartDriversFromPersistenceAsync()
       └─ 从 SQLite 读取所有 Driver 配置
       └─ 跳过 Enabled=false 的
       └─ PluginManager.CreateDriverInstance() 创建实例
       └─ InitializeAsync() → StartAsync() 启动每个驱动
       └─ 驱动的 DataSink 指向 Pipeline.Writer
       └─ 启动 DriverWatchdog 看门狗

步骤11 指标更新循环（PeriodicTimer，每5秒）
       └─ 更新 ActiveDrivers / ActivePublishers / ChannelCount
```

### 关键设计：先 Publishers 后 Drivers

驱动一旦启动就开始产出数据，如果发布器还没就绪，数据会积压在 Channel 甚至被丢弃。先确保"出口"畅通，再打开"入口"。

### 停止流程（反向优雅关闭）

```
阶段1  停止所有驱动（关闭入口，不再产生新数据）
阶段2  排空管道残余数据（等待 PendingCount == 0，超时10s）
阶段3  停止消费循环（pipeline.StopAsync）
阶段4  停止所有发布器（关闭出口）
阶段5  卸载所有插件（PluginManager.UnloadAll）
```

### 整体数据流

```
Drivers（采集） → Channel.Writer → Channel → Channel.Reader → Pipeline
    → TransformEngine → RouteResolver → Publishers（发布）
```

### 推荐阅读顺序

`Program.cs` → `PluginManager` → `GatewayRuntime` → `ChannelDataPipeline`

---

## 五、bin 与 obj 目录的区别

### bin/ — 最终输出目录

存放**编译后的成品**，即可以直接运行的文件：

```
bin/Debug/net8.0/
├── AmGateway.exe                      ← 可执行文件
├── AmGateway.dll                      ← 主程序集
├── AmGateway.Abstractions.dll         ← 依赖的程序集
├── AmGateway.deps.json                ← 依赖清单
├── AmGateway.runtimeconfig.json       ← 运行时配置
├── appsettings.json                   ← 配置文件（复制过来的）
└── Serilog.dll                        ← NuGet 依赖的 DLL
```

- **Debug** 目录：调试版本，含调试信息，未优化
- **Release** 目录：发布版本，优化过，体积更小性能更好

### obj/ — 中间产物目录

存放**编译过程中的临时文件**：

```
obj/Debug/net8.0/
├── AmGateway.csproj.nuget.dgspec.json ← NuGet 依赖规格
├── AmGateway.csproj.nuget.g.props     ← NuGet 生成的 MSBuild 属性
├── AmGateway.AssemblyInfo.cs          ← 自动生成的程序集信息
├── Reference.cs                       ← 源码生成器生成的代码
├── *.cache                            ← 增量编译缓存
├── apphost.exe                        ← 本地开发的启动宿主
└── Ref/                               ← 引用程序集（编译用，只含公开 API 签名）
```

### 核心区别

| | `bin/` | `obj/` |
|---|---|---|
| 存的是什么 | 最终编译产物 | 编译中间文件 |
| 能不能直接跑 | 能 | 不能 |
| 什么时候生成 | 编译最后阶段 | 编译过程中 |
| 缺了会怎样 | 重新编译就好 | 重新编译就好 |
| 要不要提交 Git | 不用（.gitignore 已排除） | 不用 |

### 为什么都有 Debug？

编译配置决定生成路径，两套互不干扰：

- **Debug** 配置 → `bin/Debug/` + `obj/Debug/`
- **Release** 配置 → `bin/Release/` + `obj/Release/`

### obj/ 的核心作用：增量编译

MSBuild 编译时对比 `obj/` 中的缓存，源文件没改就跳过，只重新编译变化的部分，最后链接输出到 `bin/`。删掉 `obj/` 会触发全量重新编译。

---

## 六、OPC UA NodeId 格式详解

### 什么是 NodeId

OPC UA 中每个节点（数据点、对象、方法）在 AddressSpace 中都有一个唯一标识，称为 **NodeId**。NodeId 有多种字符串表示格式。

### 标准字符串格式

```
ns=2;s=Sensors_Temperature
│   │ └── 节点在 AddressSpace 中的唯一标识符
│   └── s = string，表示 Identifier 是字符串类型
└── ns = 2，Namespace Index（命名空间索引为 2）
```

### Identifier 类型前缀

| 前缀 | 含义 | 示例 |
|------|------|------|
| `s=` | **String**（字符串） | `ns=2;s=Sensors_Temperature` |
| `i=` | **Integer**（整型） | `ns=2;i=1234` |
| `g=` | **Guid**（全局唯一标识符） | `ns=2;g=...` |
| `b=` | **ByteString**（字节串） | `ns=2;b=AQID...` |

### Namespace（命名空间）

- `ns=` 后面的数字是 **Namespace Index**，指向 OPC UA Server 定义的命名空间表中的第几项
- `ns=0` — OPC UA 标准命名空间（如 `Server`、`RootFolder`）
- `ns=1` — 通常是 DI（Device Information）规范命名空间
- `ns=2` 及以上 — 厂商自定义命名空间，具体含义取决于 Server 实现文档

### 你的配置示例

```json
"Nodes": [
  { "NodeId": "ns=2;s=Sensors_Temperature", "Name": "Temperature" },
  { "NodeId": "ns=2;s=Sensors_Pressure", "Name": "Pressure" }
]
```

含义：在 Server 的 `namespace[2]` 中，名为 `Sensors_Temperature` 的温度传感器节点（字符串标识符）。

> 建议用 UaExpert 等 OPC UA 客户端连接到 Server，先浏览 AddressSpace 确认 `ns=2` 对应的命名空间 URI，以及各节点的 NodeId 和数据类型。

---

## 七、OPC UA 订阅层级与创建顺序

### 层级结构

```
Session（会话）
  └── Subscription（订阅，一次性创建）
        └── MonitoredItem[]（监控项，添加到订阅）
```

### 正确创建顺序

```csharp
// 1. 建立 Session 连接
var session = await sessionFactory.CreateAsync(...);

// 2. 创建 Subscription 对象（设置采样间隔、发布间隔等）
var subscription = new Subscription(session.DefaultSubscription)
{
    DisplayName = "MySubscription",
    PublishingInterval = 1000,    // 发布间隔 ms
    PublishingEnabled = true,
};

// 3. 创建所有 MonitoredItem，添加到 Subscription
foreach (var node in _config.Nodes)
{
    var monitoredItem = new MonitoredItem(subscription.ResendNumber)
    {
        DisplayName = node.Name,
        NodeId = node.NodeId,
        AttributeId = Attributes.Value,
    };
    subscription.AddItem(monitoredItem);
}

// 4. 将 Subscription 添加到 Session 并创建
session.AddSubscription(subscription);
await subscription.CreateAsync();

// 5. SDK 自动开始发布，数据变化时触发 OnMonitoredItemNotification
```

### 为什么不能反过来

`MonitoredItem` 必须在创建时绑定到具体的 `Subscription` 实例（通过 `subscription.ResendNumber`）。如果先创建 MonitoredItem 再创建 Subscription，MonitoredItem 没有归属的 Subscription，`CreateAsync` 会失败。

### OPC UA vs Modbus 核心区别

| | Modbus | OPC UA |
|---|---|---|
| 通信模式 | 客户端主动轮询（Client → Server） | 服务端主动推送（Server → Client） |
| 订阅时机 | 驱动启动时读取一次，后续循环轮询 | Session 建立后订阅一次，Server 主动通知 |
| 数据推送 | 固定间隔读全部寄存器 | 只推送数据变化，节省带宽 |
| 连接管理 | 每次轮询都发请求 | 长连接 + KeepAlive 心跳 |
| 数据量 | 无论数据是否变化都读取 | 只传输变化的数据 |

---

## 八、Modbus vs S7 协议对比

### 相似之处

| 方面 | Modbus | S7 |
|------|--------|----|
| 整数字节序 | 大端（Big Endian） | **大端**（相同） |
| 数据模型 | 寄存器/位 | 数据块/位 |
| 通信方式 | 客户端请求 → 服务端响应 | 客户端请求 → 服务端响应 |
| 连接方式 | TCP Socket 直连 | TCP Socket 直连 |

---

### 连接层差异

```
Modbus TCP: 直接 TCP Socket → Modbus 帧

S7:         TCP → RFC 1006 (ISO-on-TCP) → S7 协议层 → Job/Ack 协议
```

S7 在 TCP 上封装了一层 ISO 协议，连接建立更复杂。

---

### 整数一样，浮点数大不同

S7 的 float/real 编码是**字交换**（不是简单的字节反序）：

```
标准 IEEE 754 Big-Endian（Modbus）:
[AA][BB][CC][DD]  ← 字节顺序就是内存顺序

S7 Float (STEP 7 / sharp7):
[BB][AA][DD][CC]  ← 两个 16 位字内部交换，但字内字节不变
```

所以处理 S7 浮点数需要做 **Word Swap（字交换）**，不是简单的 Reverse Bytes。

---

### 数据类型复杂度

| | Modbus | S7 |
|---|---|---|
| 支持类型 | 只读寄存器/输入寄存器/位 | Bool, Byte, Word, DWord, Int, DInt, Real, String, Date, Time, Counter, Timer |
| 数据块概念 | 无，线性地址空间 | 有 DB（Data Block）概念，必须指定 DB 编号 |
| 寻址方式 | `Address=0` | `DB1.DBD0`（数据块.数据类型.偏移） |

---

### 功能码 vs Job Type

Modbus 用功能码区分读写，S7 用不同的 **Job Type**：

| Modbus | S7 |
|--------|----|
| FC01 读线圈 | BSEND/BRECV（通信） |
| FC03 读保持寄存器 | Read Var（读取变量） |
| FC06 写单个寄存器 | Write Var（写入变量） |
| FC15 写多个线圈 | - |
| FC16 写多个寄存器 | - |

---

### 认证和保护

S7 PLC 可以设置**读写保护等级**（0-3 级），需要提供密码才能访问受保护的数据块。Modbus 无此机制。

---

### 总结

```
Modbus  = 简单请求/响应 + 线性寄存器空间 + IEEE 浮点
S7      = ISO封装 + 数据块概念 + 非标准浮点编码 + 认证保护 + 多种数据类型
```

大小端只是一小部分，核心差异在于**数据块组织方式**、**浮点编码规则**和**协议封装层**。用 S7 库（如 `sharp7`）时，库通常帮你处理了字交换，但理解原理有助于排查数据异常。

---

## 九、SQLite 并发写入与事务控制

### 单个 `Save*Async` 不需要手动加锁

SQLite 的**单条 SQL 语句本身具有原子性**。当执行 `INSERT`/`UPDATE`/`DELETE` 时，SQLite 会自动获取数据库锁，执行完释放。多个线程同时调用 `SaveDriverAsync` 时：

1. 线程 A 获取排他锁，执行 INSERT
2. 线程 B 尝试获取锁，**自动阻塞等待**
3. 线程 A 释放锁
4. 线程 B 获取锁，执行 INSERT

数据不会损坏，这是 SQLite 引擎层面保证的。

### 但存在两个真实风险

#### 风险 1：Busy Timeout 默认为 0，并发写入直接抛异常

```
Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 5: database is locked
```

**解决方案**：连接字符串增加 `Busy Timeout`：

```csharp
_connectionString = $"Data Source={dbPath};Busy Timeout=5000;";
```

第二个写入请求会等待 5 秒，而不是立刻崩溃。

#### 风险 2：多操作组合（如批量导入）没有事务包裹

`ImportFromJsonAsync` 逐条调用 `Save*Async`，而每个 `Save*Async` 内部都是**独立事务 + 独立连接**：

```csharp
// 导入 100 条 = 100 次 BEGIN + INSERT + COMMIT + 连接开关
```

中途失败（磁盘满、进程终止）时，前 N 条已写入，后 M 条没有——**数据库处于半完成的不一致状态**。

**解决方案**：用**显式事务**包裹整个导入过程，复用同一个连接：

```csharp
await using var conn = new SqliteConnection(_connectionString);
await conn.OpenAsync();
await using var transaction = await conn.BeginTransactionAsync();

try
{
    // 复用同一个连接和事务，逐条执行
    await SaveDriverAsyncInternal(conn, transaction, record);
    await SavePublisherAsyncInternal(conn, transaction, record);
    // ...

    await transaction.CommitAsync();
}
catch
{
    await transaction.RollbackAsync();
    throw;
}
```

要么全部成功，要么全部回滚。

### 什么时候需要应用层加锁

| 场景 | 是否需要加锁 | 原因 |
|------|-------------|------|
| 单个 `Save*Async` | **不需要** | SQLite 单条语句原子性保证 |
| 单个 `Delete*Async` | **不需要** | SQLite 单条语句原子性保证 |
| `ImportFromJsonAsync` | **需要事务** | 多操作组合，需保证原子性 |
| 高并发写入 | 建议加 Busy Timeout | 避免 `database is locked` 异常 |
| 业务层强一致性 | 可选 `SemaphoreSlim` | 串行化写入，避免竞态 |

### 最低限度建议

1. 连接字符串加 `Busy Timeout=5000`
2. `ImportFromJsonAsync` 改成事务模式 + 连接复用
3. 这样 90% 的并发问题都解决了

---

## 十、GatewayHostService 与 GatewayRuntime 的职责分层

### 为什么要分开？

| 层 | 类 | 职责 | 生命周期角色 |
|---|---|------|------------|
| 生命周期层 | `GatewayHostService` | 编排启停顺序、指标循环、管道排空 | 宿主的"调度者" |
| 业务状态层 | `GatewayRuntime` | 持有驱动/发布器实例、增删改查、持久化 | 系统的"核心模型" |

### 合并的问题

**1. BackgroundService 的契约不适合暴露业务方法**

`BackgroundService` 的契约是 `ExecuteAsync` / `StopAsync`，关注的是"什么时候启动、什么时候停止"。如果把 `AddDriverAsync`、`RemovePublisherAsync` 也塞进去，这个类既管生命周期又管业务状态，违反单一职责。

**2. API 无法直接访问 BackgroundService**

所有 Minimal API 端点直接注入 `GatewayRuntime`，不走 `GatewayHostService`：

```csharp
// ApiEndpoints.cs — 直接注入 GatewayRuntime
api.MapGet("/drivers", (GatewayRuntime runtime) =>
{
    return Results.Ok(runtime.GetDriverInfos());
});

api.MapPost("/drivers", async (CreateDriverRequest request, GatewayRuntime runtime, ...) =>
{
    var info = await runtime.AddDriverAsync(...);
});
```

如果业务逻辑全在 `GatewayHostService` 里，API 端点就得注入一个 `BackgroundService` 来调用业务方法，架构上很别扭。

**3. 分开后的调用关系很清晰**

```
Program.cs
  ├─ 注册 GatewayRuntime (Singleton)
  ├─ 注册 GatewayHostService (BackgroundService)
  └─ 注册 API 端点 → 注入 GatewayRuntime

GatewayHostService（编排者）
  ├─ 启动时调用 runtime.SeedFromAppSettingsAsync()
  ├─ 启动时调用 runtime.StartPublishersFromPersistenceAsync()
  ├─ 启动时调用 runtime.StartDriversFromPersistenceAsync()
  └─ 停止时调用 runtime.RemoveDriverAsync() / RemovePublisherAsync()

API 端点（业务操作）
  ├─ runtime.AddDriverAsync()       ← 动态添加驱动
  ├─ runtime.RemovePublisherAsync()  ← 动态删除发布器
  ├─ runtime.GetDriverInfos()        ← 查询状态
  └─ runtime.AddTransformRuleAsync() ← 管理规则
```

### GatewayRuntimeAccessor 的设计问题

`GatewayRuntimeAccessor` 是一个静态可变状态类，目前只有 `GatewayHostService` 在使用。实际上：

- **Minimal API 不需要它** — 通过 DI 参数绑定直接注入 `GatewayRuntime`
- **GatewayHostService 不需要它** — 可以通过构造函数注入 `GatewayRuntime`
- 它的存在让依赖关系变得隐式，不利于测试

**建议重构方向**：`GatewayHostService` 构造函数直接注入 `GatewayRuntime`，删除 `GatewayRuntimeAccessor`。

### 总结

`GatewayRuntime` 是共享的业务核心，`GatewayHostService` 和 `API` 是两个不同的"消费者"——一个管生命周期，一个管外部操作。把它们分开，`Runtime` 才能被两方独立使用而不互相干扰。

---

## 十一、V2.0 代码审查问题分类

### 背景

对 V2.0 全部 15 个模块的 todo 文件进行审查，共识别约 110+ 个问题条目，按问题类型归纳为 11 类。

### 问题类型总览

| 类型 | 数量 | 严重性 | 涉及模块 |
|------|------|--------|----------|
| 并发/竞态 | ~15 | 🔴高 | Runtime、Pipeline、HostService、InfluxDB、MQTT、Transform、Sqlite、Plugin、Route |
| 连接/重连 | ~10 | 🔴高 | Modbus、S7、OPC UA、MQTT |
| 数据丢失 | ~8 | 🔴高 | InfluxDB、MQTT、Pipeline、Runtime、HostService |
| 安全漏洞 | ~8 | 🔴高 | Auth、API、OPC UA、Transform |
| 资源泄漏 | ~7 | 🔴高 | S7、InfluxDB、Modbus、Plugin |
| 性能瓶颈 | ~15 | 🟡中 | Pipeline、Route、Transform、S7、Sqlite、Metrics |
| 异常处理 | ~10 | 🟡中 | Runtime、Pipeline、S7、Sqlite、API、Auth |
| 配置问题 | ~12 | 🟡中 | Modbus、OPC UA、S7、InfluxDB、MQTT、Pipeline、Metrics |
| API 设计 | ~8 | 🟡中 | ApiEndpoints、Auth |
| 代码结构 | ~10 | 🟢低 | Runtime、ApiEndpoints、Modbus、OPC UA |
| 可观测性 | ~7 | 🟢低 | Modbus、Route、Metrics、Auth |

### 各类型详解

#### 1. 并发/竞态（~15 条，最高频）

几乎每个模块都有，核心模式是"读-改-写非原子"或"多个异步路径同时操作同一状态"：

| 模块 | 问题 |
|------|------|
| Runtime | AddPublisher 异常时 Pipeline 未清理；Remove 先删字典再停实例；规则热更新读-改-写竞态 |
| Pipeline | 慢发布器阻塞整条流水线（串行 await） |
| HostService | StopAsync 与 ExecuteAsync 竞态，base.StopAsync 顺序反了 |
| InfluxDB | Timer 同步调用 FlushBatchAsync 导致并发 flush |
| MQTT | StopAsync 与重连循环竞态 |
| Transform | Jint 引擎非线程安全，多线程共享会数据串扰 |
| Sqlite | `_initialized` 非线程安全；每次操作新建连接无复用 |
| Plugin | GetLoadedDriverPlugins 返回内部字典引用 |
| Route | LoadRules 全量替换期间 Resolve 可能读到不一致状态 |

#### 2. 连接/重连（~10 条，驱动层共性问题）

三个驱动 + MQTT 发布器都有类似问题，缺少"连接超时 + 存活检测 + 指数退避重连"三板斧：

| 模块 | 缺失项 |
|------|--------|
| Modbus | 无连接超时、无连接存活检测、无指数退避重连 |
| S7 | OpenAsync 无超时可能永久阻塞、重连时旧 Plc 未释放、无指数退避 |
| OPC UA | Session 无重连机制、断线后永久失效 |
| MQTT | 重连循环无上限、UseTls 配置未生效 |

#### 3. 数据丢失（~8 条，工业场景最不可接受）

核心模式：缺少"缓冲 + 重试 + 兜底"，失败就丢：

| 模块 | 问题 |
|------|------|
| InfluxDB | 写入失败直接丢弃，无重试/重排队列 |
| MQTT | 断线期间数据静默丢弃，无缓冲 |
| Pipeline | DropOldest 策略 + 慢发布器阻塞 = 大面积丢数据 |
| Runtime | Remove 先删字典，Stop 失败后实例失控但无法管理 |
| HostService | 停止超时硬编码，总时长可能超限被强制杀死 |

#### 4. 安全漏洞（~8 条，生产环境红线）

核心模式：开发便利性优先，安全防护缺失：

| 模块 | 问题 |
|------|------|
| Auth | 硬编码 admin/admin、密码明文比对、默认密钥写在代码中 |
| Auth | 无登录速率限制、无 Token 撤销机制 |
| API | /shutdown 任何人可关机、/config/import 无校验可注入 |
| OPC UA | AutoAcceptUntrustedCertificates=true、SecurityPolicy 配置未生效 |
| Transform | Jint 脚本引擎未限制危险 API |

#### 5. 资源泄漏（~7 条）

核心模式：覆盖引用前未先释放旧资源：

| 模块 | 问题 |
|------|------|
| S7 | 重连时旧 Plc 未 Close，Socket 泄漏 |
| InfluxDB | SocketsHttpHandler 未 Dispose，TCP 连接池泄漏 |
| Modbus | TcpClient 重连时未先关闭旧连接 |
| Plugin | LoadContext.Unload 非立即生效，DLL 文件仍被锁定 |

#### 6. 性能瓶颈（~15 条）

核心模式："每次都重建/重算"，缺少预编译/预排序/缓存：

| 模块 | 问题 |
|------|------|
| Pipeline | 串行扇出、发布器无独立超时 |
| Route | 每次 Resolve 重复 OrderBy、IsTagMatch 每次编译正则、每次创建新 HashSet |
| Transform | IsTagMatch 同样每次编译正则、分段线性每次 Sort、规则全表扫描 |
| S7 | DataItems 逐条读取，应按 DB 批量读取 |
| Sqlite | Import 逐条保存无事务、每次操作新建连接、Export 不必要的反序列化-再序列化 |
| Metrics | 延迟分位数用 gauge 不符合 Prometheus histogram 规范 |

#### 7. 异常处理（~10 条）

核心模式：异常被吞或未被捕获，导致上层无法感知失败：

| 模块 | 问题 |
|------|------|
| Runtime | RestartDriver 静默返回不抛异常 |
| Pipeline | Transform/Resolve 异常未捕获，消费循环可能崩溃 |
| S7 | Disconnect() 可能抛异常，StopAsync 被中断 |
| Sqlite | 所有 DB 操作缺异常处理、ImportFromJson 中 Enum.Parse 无保护 |
| API | 异常处理不一致 |
| Auth | 登录失败无日志记录 |

#### 8. 配置问题（~12 条）

核心模式：配置字段"声明了但没接上"或"硬编码应该可配置"：

| 模块 | 问题 |
|------|------|
| Modbus | RegisterDefinition.Type 用字符串无编译期检查 |
| OPC UA | SecurityPolicy 字段存在但未使用、超时硬编码、QueueSize 未配置化 |
| S7 | CpuType 无校验、默认值与库枚举可能不匹配 |
| InfluxDB | ReconnectDelayMs 未使用、Gzip 半实现 |
| MQTT | UseTls 未生效、无 KeepAlive 配置 |
| Pipeline | Channel 容量硬编码 10000 |
| Metrics | ChannelCapacity 与实际不同步 |

#### 9. API 设计（~8 条）

| 模块 | 问题 |
|------|------|
| ApiEndpoints | 请求模型缺输入校验、Settings 序列化绕一圈、异常处理不一致 |
| ApiEndpoints | 匿名返回类型无 OpenAPI 契约、直接暴露 ConfigRepo |
| Auth | 全局默认无认证、所有用户都是 Admin |

#### 10. 代码结构（~10 条）

| 模块 | 问题 |
|------|------|
| Runtime | 驱动/发布器增删启停代码高度重复、BuildConfigurationFromJsonPublic 命名尴尬 |
| ApiEndpoints | 330 行单方法、Request 与 Endpoints 同文件 |
| Modbus | ModbusFactory 每次 new |
| OPC UA | ActivitySource/Meter 每实例重复创建 |

#### 11. 可观测性（~7 条）

| 模块 | 问题 |
|------|------|
| API | 健康检查永远 healthy |
| Route | 缺少命中/未命中调试日志 |
| Metrics | 计数器无 label 维度、缺进程级指标 |
| Modbus | 缺采集指标统计 |
| Auth | 登录失败无日志 |

### 修复建议优先级

1. **第一批（安全+数据丢失）**：Auth 安全加固、Pipeline 并行扇出 + 异常保护、InfluxDB/MQTT 数据缓冲 + 重试
2. **第二批（并发+连接）**：Runtime 竞态修复、驱动连接三板斧（超时+存活+退避）、HostService 停机顺序
3. **第三批（性能）**：Route/Transform 正则预编译、Sqlite 事务+连接复用、S7 批量读取
4. **第四批（API+配置）**：输入校验、配置字段对齐、异常处理统一
5. **第五批（结构+可观测）**：代码拆分、指标维度、健康检查

---

## 十二、从"能跑"到"能上线"——软件质量演进规律

### 为什么这些问题在初期容易被忽略

软件开发有个自然的演进节奏，每个阶段关注点不同：

**第一阶段：让逻辑跑通**
- 关注"功能对不对"：数据能不能采集、能不能发送、API 能不能响应
- 心智模型是 "happy path"——假设一切正常
- 并发、异常、断线、资源泄漏？不会考虑，因为本地测试根本遇不到

**第二阶段：让程序稳住**
- 逻辑跑通了，但一上线就出问题
- 典型症状：跑几小时就崩、断网后再也连不上、内存越跑越高
- 对应最高频的几类问题：**并发竞态、连接重连、资源泄漏、异常处理**

**第三阶段：让程序安全**
- 安全漏洞在开发环境完全不会暴露（admin/admin 能登录就行）
- 只有面对真实用户/网络时才是问题
- 对应：**硬编码凭据、无认证端点、脚本引擎未沙箱**

**第四阶段：让程序快**
- 初期数据量小，性能问题看不出来
- 对应：**正则每次编译、逐条读取、串行扇出**

**第五阶段：让程序可观测**
- "跑得好好的怎么突然不行了？"——缺指标、缺日志
- 对应：**健康检查假 healthy、缺维度指标**

### 核心规律

> **初期能跑通 = 只覆盖了 happy path；这些待修复问题的本质，全是 sad path 和 edge case。**

工业场景（网关采集）尤其如此——网络断、设备离线、数据爆发、长时间运行——这些在生产环境是常态而非例外。

### 通用启示

| 开发阶段 | 关注点 | 容易忽略 | 对应问题类型 |
|----------|--------|----------|-------------|
| 逻辑跑通 | 功能正确性 | 错误路径、边界条件 | 异常处理、配置问题 |
| 稳定性 | 长时间运行 | 竞态、泄漏、断线 | 并发/竞态、连接/重连、资源泄漏 |
| 安全性 | 访问控制 | 凭据暴露、注入攻击 | 安全漏洞 |
| 性能 | 高吞吐 | 重复计算、无效 I/O | 性能瓶颈 |
| 可观测 | 故障定位 | 指标维度、日志细节 | 可观测性 |





