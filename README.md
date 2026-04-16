# AmGateway - 工业物联网网关

基于 .NET 10 的插件化工业网关，支持多协议数据采集、实时数据管道、多目标发布。

## 架构概览

```
┌─────────────┐     ┌──────────────────────────────────┐     ┌──────────────┐
│  工业设备    │     │          AmGateway               │     │  数据消费端   │
│             │     │                                  │     │              │
│ Modbus TCP ─┼────►│ Driver.Plugin ──► DataPipeline ──┼────►│ MQTT Broker  │
│ OPC UA     ─┼────►│ (采集+转换+路由)  (Channel+批处理) │     │ InfluxDB     │
│ S7/CIP     ─┼────►│                                  │     │ ...          │
└─────────────┘     └──────────────────────────────────┘     └──────────────┘
```

### 核心组件

| 组件 | 说明 |
|------|------|
| **PluginManager** | 插件发现与加载，支持热插拔驱动/发布器 |
| **GatewayRuntime** | 运行时管理：驱动/发布器的创建、启停、重启 |
| **ChannelDataPipeline** | 基于 `System.Threading.Channels` 的异步数据管道 |
| **TransformEngine** | 数据转换引擎（Glob 匹配 + 表达式计算） |
| **RouteResolver** | 路由规则引擎，将数据点分发到指定发布器 |
| **GatewayHostService** | IHostedService，管理网关生命周期和优雅关机 |
| **DriverWatchdog** | 驱动看门狗，自动恢复异常驱动 |
| **SqliteConfigRepository** | SQLite 持久化，驱动/发布器/路由/转换配置 |

## 已实现功能

### 驱动（数据采集）

| 协议 | 插件 | 状态 | 说明 |
|------|------|------|------|
| Modbus TCP | `AmGateway.Driver.Modbus` | ✅ 可用 | 支持多寄存器轮询、断线自动重连 |
| OPC UA | `AmGateway.Driver.OpcUa` | ✅ 可用 | 支持订阅模式、MonitoredItem 创建检查 |
| S7 (Siemens) | `AmGateway.Driver.S7` | 🔧 框架 | 基础结构已有，待接入实际 PLC |
| CIP (Allen-Bradley) | `AmGateway.Driver.CIP` | 🔧 框架 | 基础结构已有，待接入实际 PLC |

### 发布器（数据输出）

| 传输 | 插件 | 状态 | 说明 |
|------|------|------|------|
| MQTT | `AmGateway.Publisher.Mqtt` | ✅ 可用 | 支持断线自动重连 |
| InfluxDB | `AmGateway.Publisher.InfluxDB` | ✅ 可用 | 批量写入、可配置刷新间隔 |

### 数据管道

- **Channel 异步队列**：生产者-消费者模式，背压控制（容量 10000）
- **数据转换**：Glob 模式匹配 + 表达式计算（如 `value + 100`）
- **数据路由**：按 tag 模式将数据点分发到指定发布器

### REST API

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/auth/login` | POST | JWT 认证登录 |
| `/api/health` | GET | 网关健康状态 |
| `/api/drivers` | GET/POST | 驱动列表 / 创建驱动 |
| `/api/drivers/{id}` | GET/DELETE | 驱动详情 / 删除驱动 |
| `/api/drivers/{id}/restart` | POST | 重启驱动 |
| `/api/publishers` | GET/POST | 发布器列表 / 创建发布器 |
| `/api/publishers/{id}` | GET/DELETE | 发布器详情 / 删除发布器 |
| `/api/transforms` | GET/POST | 转换规则列表 / 创建规则 |
| `/api/transforms/{id}` | DELETE | 删除转换规则 |
| `/api/routes` | GET/POST | 路由规则列表 / 创建规则 |
| `/api/routes/{id}` | DELETE | 删除路由规则 |
| `/api/shutdown` | POST | 优雅关机 |

### 关键特性

- **断线重连**：Modbus 驱动读取失败后自动断开 → 延迟重连 → 恢复数据
- **优雅关机**：`/api/shutdown` 触发按序停止（驱动 → 管道排空 → 发布器），380ms 内完成
- **配置持久化**：SQLite 存储，重启后自动恢复所有驱动和发布器
- **看门狗**：自动检测异常驱动并尝试恢复

## 快速开始

### 前置条件

- .NET 10 SDK
- Mosquitto MQTT Broker（默认 localhost:1883）
- InfluxDB 2.x（可选，默认 localhost:8086）
- AmVirtualSlave（Modbus 从站模拟器，端口 5020）
- AmVirtualSlave OPC UA 服务（端口 4841）

### 构建

```powershell
# 编译主项目
dotnet build AmGateway/AmGateway.csproj -c Debug

# 编译插件（必须单独编译）
dotnet build Plugins/AmGateway.Driver.Modbus/AmGateway.Driver.Modbus.csproj -c Debug
dotnet build Plugins/AmGateway.Driver.OpcUa/AmGateway.Driver.OpcUa.csproj -c Debug

# 复制插件 DLL 到运行目录
Copy-Item "Plugins/AmGateway.Driver.Modbus/bin/Debug/net10.0/AmGateway.Driver.Modbus.dll" `
  "AmGateway/bin/Debug/net10.0/plugins/AmGateway.Driver.Modbus/" -Force
Copy-Item "Plugins/AmGateway.Driver.OpcUa/bin/Debug/net10.0/AmGateway.Driver.OpcUa.dll" `
  "AmGateway/bin/Debug/net10.0/plugins/AmGateway.Driver.OpcUa/" -Force
```

### 运行

```powershell
cd AmGateway/bin/Debug/net10.0
./AmGateway.exe
```

### 验证

```powershell
# 登录获取 token
$token = (Invoke-RestMethod -Uri 'http://localhost:5002/api/auth/login' `
  -Method Post -ContentType 'application/json' `
  -Body '{"username":"admin","password":"admin"}').token

# 检查健康状态
Invoke-RestMethod -Uri 'http://localhost:5002/api/health' -Headers @{Authorization="Bearer $token"}

# 查看 MQTT 数据
& "C:\Program Files\mosquitto\mosquitto_sub.exe" -h localhost -p 1883 -t "amgateway/#" -C 4 -W 5
```

## 项目结构

```
AmGateway/
├── AmGateway/                      # 主项目
│   ├── Program.cs                  # 入口，DI 配置
│   ├── appsettings.json            # 默认配置（驱动/发布器/认证）
│   ├── Pipeline/                   # 数据管道
│   │   ├── ChannelDataPipeline.cs  # Channel 实现
│   │   ├── TransformEngine.cs      # 数据转换引擎
│   │   └── RouteResolver.cs        # 路由规则引擎
│   ├── Services/                   # 核心服务
│   │   ├── GatewayRuntime.cs       # 运行时管理
│   │   ├── GatewayHostService.cs   # 生命周期 + 优雅关机
│   │   ├── ApiEndpoints.cs         # REST API 端点
│   │   ├── DriverWatchdog.cs       # 驱动看门狗
│   │   └── SqliteConfigRepository.cs  # 配置持久化
│   └── PluginHost/
│       └── PluginManager.cs        # 插件发现与加载
├── Plugins/                        # 插件项目
│   ├── AmGateway.Driver.Modbus/    # Modbus TCP 驱动
│   ├── AmGateway.Driver.OpcUa/     # OPC UA 驱动
│   ├── AmGateway.Driver.S7/        # S7 驱动（框架）
│   ├── AmGateway.Driver.CIP/       # CIP 驱动（框架）
│   ├── AmGateway.Publisher.Mqtt/   # MQTT 发布器
│   └── AmGateway.Publisher.InfluxDB/ # InfluxDB 发布器
├── TESTPLAN.md                     # 测试计划与结果（37/37 通过）
└── TROUBLESHOOTING.md              # 排障记录
```

## 配置说明

### appsettings.json 关键配置

```json
{
  "Gateway": {
    "PluginsPath": "plugins",           // 插件目录（相对于运行目录）
    "ShutdownTimeoutSeconds": 30,       // 优雅关机超时
    "DrainTimeoutSeconds": 10           // 管道排空超时
  },
  "Authentication": {
    "Jwt": {
      "Enabled": true,
      "SecretKey": "请修改为安全密钥",
      "ExpireHours": 24
    }
  }
}
```

### 默认账号

- 用户名：`admin`，密码：`admin`（生产环境请修改）

## 测试结果

**37/37 测试全部通过**，详见 [TESTPLAN.md](TESTPLAN.md)。

### 已修复的关键 Bug

| # | Bug | 影响 |
|---|-----|------|
| B1 | 插件 DLL 发现路径错误 | 驱动无法加载 |
| B2 | 重复添加驱动未报错 | 配置混乱 |
| B3 | 协议名大小写不匹配 | API 创建驱动失败 |
| B4 | Modbus 断线无法自动重连 | 从站恢复后数据仍 Bad |
| B5 | InfluxDB Org/Bucket 配置不匹配 | 写入 404 |
| B6 | 优雅关机无法通过信号触发 | 强杀丢失数据 |
| B7 | 插件 DLL 编译后未自动复制 | 代码修改不生效 |

详细排障记录见 [TROUBLESHOOTING.md](TROUBLESHOOTING.md)。

## License

Private - Internal Use Only
