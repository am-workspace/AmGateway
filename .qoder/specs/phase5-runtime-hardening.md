# Phase 5: 运行时加固（边跑边补）

> 前提：Phase 1-4 核心功能和安全冗余已全部实现，网关可正常运行。
> 本阶段为**运行中逐步补齐**的增强项，按优先级排列。

---

## 当前已实现能力清单

| 能力 | 状态 | 实现 |
|------|------|------|
| 南向协议驱动 (4种) | ✅ | Modbus TCP, OPC UA, S7, CIP — 插件化 |
| 北向发布器 (2种) | ✅ | MQTT + InfluxDB — 插件化 |
| 数据管道 (缓冲+扇出) | ✅ | Channel 10000 + DropOldest + Transform + Route |
| 数据转换引擎 | ✅ | 线性/分段/单位换算/死区/JS脚本 |
| 数据路由引擎 | ✅ | Glob 匹配 + 优先级 + 目标发布器 |
| REST API 动态配置 | ✅ | 驱动/发布器/转换/路由 CRUD + 配置导出/导入 |
| JWT 认证 | ✅ | 可开关 + 登录端点 |
| 配置持久化 | ✅ | SQLite — 重启后恢复 |
| 驱动看门狗自愈 | ✅ | 指数退避 + 5次重试 |
| Prometheus Metrics | ✅ | /metrics 端点 + 12项指标 |
| 优雅关机 | ✅ | 5阶段：驱停→排空→消停→发停→卸载 |
| 健康检查 | ✅ | /api/health 含指标摘要 |
| 驱动断线重连 | ✅ | 所有驱动 ReconnectDelayMs + 自动重连 |
| MQTT 断线重连 | ✅ | DisconnectedAsync 事件触发无限重试 |
| InfluxDB 批量缓冲 | ✅ | BatchSize + FlushInterval + Stop 时 flush |
| 管道背压 | ✅ | BoundedChannel + DropOldest |
| 发布器/驱动异常隔离 | ✅ | 单个异常不影响其他 |
| ALC 插件隔离 | ✅ | 每个插件独立 AssemblyLoadContext |

---

## 任务列表

### P1: OPC UA 安全收紧

**优先级：中 — 对接 OPC UA 设备前必须处理**

- [ ] 移除 `AutoAcceptUntrustedCertificates = true`
- [ ] 实现证书信任列表管理（REST API）
- [ ] 支持应用证书配置（appsettings.json 或文件路径）
- [ ] 添加证书过期提醒
- [ ] API 端点：
  - `GET /api/opcua/certificates` — 列出已知证书
  - `POST /api/opcua/certificates/{thumbprint}/trust` — 信任证书
  - `DELETE /api/opcua/certificates/{thumbprint}` — 移除证书

**涉及文件：**
- `Plugins/AmGateway.Driver.OpcUa/OpcUaDriver.cs`
- `AmGateway/Services/` — 新增证书管理服务

---

### P2: IWritableDriver 南向写命令

**优先级：中 — 需要远程控制 PLC 时必须实现**

- [ ] `IWritableDriver` 接口已在 Abstractions 中预留，需要实现
- [ ] REST API 端点：
  - `POST /api/drivers/{id}/write` — 写入单个点位
  - `POST /api/drivers/{id}/write/batch` — 批量写入
- [ ] 各驱动的写实现：
  - Modbus TCP: WriteMultipleRegisters / WriteSingleCoil
  - OPC UA: WriteNode
  - S7: DBWrite
  - CIP: SetAttributeSingle
- [ ] 写入权限控制（JWT scope 扩展）

**涉及文件：**
- `AmGateway.Abstractions/IWritableDriver.cs` — 已有接口
- `Plugins/AmGateway.Driver.Modbus/` — 添加写方法
- `Plugins/AmGateway.Driver.OpcUa/` — 添加写方法
- `Plugins/AmGateway.Driver.S7/` — 添加写方法
- `Plugins/AmGateway.Driver.Cip/` — 添加写方法
- `AmGateway/Services/DriverManager.cs` — 识别 IWritableDriver

---

### P3: Publisher 离线缓冲

**优先级：中 — InfluxDB 断连时间较长时需要**

- [ ] InfluxDB 断连时，数据写入本地文件缓冲
- [ ] 连接恢复后，回放缓冲数据到 InfluxDB
- [ ] 配置项：
  ```json
  {
    "OfflineBuffer": {
      "Enabled": true,
      "Directory": "./buffer",
      "MaxSizeMB": 500,
      "Compress": true
    }
  }
  ```
- [ ] 缓冲文件格式：每行一个 LineProtocol，文件名含时间戳
- [ ] 回放策略：先写实时数据，再回放历史缓冲
- [ ] Metrics 新增：`gateway_buffer_size_bytes`, `gateway_buffer_replay_total`

**涉及文件：**
- `Plugins/AmGateway.Publisher.InfluxDB/InfluxDbPublisher.cs`
- 新增 `OfflineBuffer` 类

---

### P4: 审计日志

**优先级：低 — 合规场景需要**

- [ ] 通过 Serilog enricher 实现审计日志
- [ ] 记录内容：
  - 配置变更（谁/何时/改了什么）
  - 驱动启停
  - 发布器连接/断连
  - API 调用（含 JWT subject）
- [ ] 审计日志独立输出通道（文件或数据库）
- [ ] 配置：
  ```json
  {
    "AuditLog": {
      "Enabled": true,
      "Output": "file",
      "Path": "./logs/audit.json"
    }
  }
  ```

**涉及文件：**
- `AmGateway/Program.cs` — Serilog 配置
- 新增 `AuditLogEnricher`
- `AmGateway/Services/` — 各服务添加审计点

---

### P5: 报警/事件模型

**优先级：低 — 上位机需要报警语义时实现**

- [ ] 新增 `AlarmPoint` 模型：
  ```csharp
  public class AlarmPoint
  {
      public string TagName { get; set; }
      public AlarmSeverity Severity { get; set; }  // Info, Warning, Critical
      public AlarmState State { get; set; }         // Active, Acknowledged, Cleared
      public double? LimitValue { get; set; }
      public string Message { get; set; }
      public DateTime Timestamp { get; set; }
  }
  ```
- [ ] 在 TransformEngine 中添加阈值判断规则
- [ ] 报警通过 MQTT 发布到独立 topic
- [ ] REST API：
  - `GET /api/alarms` — 当前活跃报警
  - `POST /api/alarms/{id}/ack` — 确认报警

**涉及文件：**
- `AmGateway.Abstractions/Models/` — 新增 AlarmPoint
- `AmGateway/Pipeline/TransformEngine.cs` — 添加阈值判断
- `AmGateway/Services/` — 新增 AlarmService

---

### P6: Web 管理界面

**优先级：低 — REST API 已完备，UI 是锦上添花**

- [ ] 技术选型：嵌入式 Blazor Server 或静态 SPA
- [ ] 页面清单：
  - 仪表盘（实时指标 + 连接状态）
  - 驱动管理（列表/添加/编辑/删除/启停）
  - 发布器管理
  - 数据转换规则管理
  - 数据路由规则管理
  - Metrics 图表
  - 系统日志查看
- [ ] 嵌入方式：`app.UseStaticFiles()` + SPA 打包到 wwwroot

**涉及文件：**
- 新增 `AmGateway.Web/` 项目
- `AmGateway/Program.cs` — 挂载静态文件

---

### P7: 高可用/主备

**优先级：低 — 需要外部协调，非单机范畴**

- [ ] 架构选型：
  - 方案A：主备共享 SQLite + 文件锁
  - 方案B：etcd/Consul 选主
  - 方案C：Kubernetes Leader Election
- [ ] 选主后只有主节点采集+发布
- [ ] 备节点心跳监听，主节点失联后接管
- [ ] 需要共享配置存储

**说明：** 此项依赖部署环境，建议确定部署方案后再设计。

---

## 实施建议

```
运行中优先顺序：

  P1 OPC UA 安全收紧    ← 对接 OPC UA 前必做
  P2 南向写命令          ← 需要控制 PLC 时做
  P3 Publisher 离线缓冲  ← InfluxDB 断连频繁时做
  ───────────────────── 以下可按需 ─────────────────────
  P4 审计日志
  P5 报警/事件模型
  P6 Web 管理界面
  P7 高可用/主备
```

**原则：边跑边补，按实际需求驱动，不提前过度设计。**
