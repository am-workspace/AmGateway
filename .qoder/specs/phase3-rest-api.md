# Phase 3: REST API 动态配置

## 目标
提供 HTTP REST API，运行时热加载/卸载驱动和发布器，不需重启网关。

## 核心设计

### 架构变更
```
当前: GatewayHostService 直接持有 _activeDrivers/_activePublishers 列表
目标: 抽取 GatewayRuntime 管理运行时实例，REST Controller 通过它操作
```

### API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /api/drivers | 列出所有驱动实例（含状态） |
| GET | /api/drivers/{instanceId} | 获取单个驱动详情 |
| POST | /api/drivers | 动态创建并启动驱动 |
| DELETE | /api/drivers/{instanceId} | 停止并移除驱动 |
| GET | /api/publishers | 列出所有发布器实例（含状态） |
| GET | /api/publishers/{instanceId} | 获取单个发布器详情 |
| POST | /api/publishers | 动态创建并启动发布器 |
| DELETE | /api/publishers/{instanceId} | 停止并移除发布器 |
| GET | /api/plugins | 列出已加载的插件 |
| GET | /api/health | 健康检查 |

### 驱动/发布器状态枚举
```csharp
public enum RuntimeStatus { Starting, Running, Stopping, Stopped, Error }
```

### POST /api/drivers 请求体
```json
{
  "protocol": "opcua",
  "instanceId": "opcua-02",
  "settings": {
    "Endpoint": "opc.tcp://192.168.1.10:4840",
    "Nodes": [...]
  }
}
```

### GET /api/drivers 响应
```json
[
  {
    "instanceId": "opcua-01",
    "protocol": "opcua",
    "status": "Running",
    "startedAt": "2026-04-15T12:00:00Z"
  }
]
```

## 实现步骤（分批短链）

### P3-A: GatewayRuntime + 状态模型
1. 新增 `AmGateway.Abstractions/Models/RuntimeStatus.cs`
2. 新增 `AmGateway.Abstractions/Models/DriverInstanceInfo.cs`
3. 新增 `AmGateway.Abstractions/Models/PublisherInstanceInfo.cs`
4. 新增 `AmGateway/Services/GatewayRuntime.cs` — 从 GatewayHostService 抽取驱动/发布器管理逻辑
5. 修改 `AmGateway/Services/GatewayHostService.cs` — 委托给 GatewayRuntime
6. 构建 + 验证

### P3-B: REST API Controller
7. AmGateway.csproj 添加 ASP.NET Core 最小 API 包
8. 新增 `AmGateway/Controllers/DriversController.cs`
9. 新增 `AmGateway/Controllers/PublishersController.cs`
10. 新增 `AmGateway/Controllers/PluginsController.cs`
11. 新增 `AmGateway/Controllers/HealthController.cs`
12. 修改 `Program.cs` — 添加 ASP.NET Core Web 支持
13. 修改 `appsettings.json` — 添加 ApiHost 配置
14. 构建 + 验证

### P3-C: Pipeline 动态 Publisher 支持
15. 修改 `IDataPipeline.cs` — 新增 AddPublisher / RemovePublisher
16. 修改 `ChannelDataPipeline.cs` — 实现动态增删 Publisher（线程安全）
17. 构建 + 验证

### P3-D: 端到端集成测试
18. 全量构建
19. 清理临时文件

## 关键设计决策

1. **GatewayRuntime 为单例** — 持有所有运行时实例，线程安全
2. **驱动/发布器实例按 InstanceId 索引** — ConcurrentDictionary 存储
3. **动态添加驱动时自动注入 Pipeline.Writer** — 与启动时行为一致
4. **动态添加发布器时自动注册到 Pipeline** — 热插拔生效
5. **删除驱动/发布器时先 Stop 再 Dispose** — 优雅关闭
6. **API 端口默认 5000** — 通过 appsettings.json 配置
7. **使用 ASP.NET Core Minimal API** — 轻量，不用 Controller 类
