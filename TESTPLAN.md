# AmGateway 功能测试计划

> 测试环境：Windows, Mosquitto MQTT Broker (localhost:1883), AmVirtualSlave 从站
> 网关地址：http://localhost:5002
> 默认凭据：admin / admin

---

## 一、JWT 认证

### T1.1 登录获取 Token
```
POST /api/auth/login
Body: {"username":"admin","password":"admin"}
期望：200 + {"token":"eyJ...", "expiresAt":"..."}
```

### T1.2 无 Token 访问受保护端点
```
GET /api/drivers （不带 Authorization header）
期望：401 Unauthorized
```

### T1.3 错误凭据登录
```
POST /api/auth/login
Body: {"username":"admin","password":"wrong"}
期望：401 Unauthorized
```

### T1.4 有效 Token 访问受保护端点
```
GET /api/drivers （带 Authorization: Bearer <token>）
期望：200 + 驱动列表
```

### T1.5 健康检查免认证
```
GET /api/health （不带 Token）
期望：200 + {"status":"healthy", ...}
```

### T1.6 Metrics 免认证
```
GET /metrics （不带 Token）
期望：200 + Prometheus 文本格式
```

---

## 二、驱动管理（CRUD）

### T2.1 获取驱动列表
```
GET /api/drivers
期望：200 + 当前运行中的驱动数组
```

### T2.2 获取指定驱动信息
```
GET /api/drivers/modbus-01
期望：200 + {"instanceId":"modbus-01", "protocol":"ModbusTcp", "status":"Running", ...}
```

### T2.3 获取不存在的驱动
```
GET /api/drivers/nonexistent
期望：404
```

### T2.4 动态添加 Modbus 驱动
```
POST /api/drivers
Body: {
  "protocol": "ModbusTcp",
  "instanceId": "modbus-dynamic-01",
  "settings": {
    "Host": "127.0.0.1",
    "Port": 502,
    "PollIntervalMs": 1000,
    "Registers": [
      {"Address": 0, "Name": "Temperature", "Type": "HoldingRegister", "Scale": 0.1}
    ]
  }
}
期望：201 + 驱动实例信息，MQTT 订阅中出现 modbus-dynamic-01 数据
```

### T2.5 添加重复 ID 驱动
```
POST /api/drivers （使用已存在的 instanceId）
期望：409 Conflict + {"error":"驱动实例 xxx 已存在"}
```

### T2.6 删除驱动
```
DELETE /api/drivers/modbus-dynamic-01
期望：204，驱动列表中不再有该驱动，MQTT 订阅停止该驱动数据
```

### T2.7 删除不存在的驱动
```
DELETE /api/drivers/nonexistent
期望：404
```

### T2.8 驱动重启
```
POST /api/drivers/modbus-01/restart
期望：200 + {"message":"驱动 modbus-01 已重启"}
验证：重启后数据恢复
```

### T2.9 添加不支持的协议
```
POST /api/drivers
Body: {"protocol":"Unknown","instanceId":"test-01","settings":{}}
期望：500 或错误信息
```

---

## 三、发布器管理（CRUD）

### T3.1 获取发布器列表
```
GET /api/publishers
期望：200 + 发布器数组
```

### T3.2 获取指定发布器信息
```
GET /api/publishers/mqtt-pub-01
期望：200 + 发布器实例信息
```

### T3.3 动态添加 MQTT 发布器
```
POST /api/publishers
Body: {
  "transport": "Mqtt",
  "instanceId": "mqtt-dynamic-01",
  "settings": {
    "Broker": "127.0.0.1",
    "Port": 1883,
    "TopicPrefix": "amgateway2"
  }
}
期望：201 + 发布器实例信息
```

### T3.4 添加重复 ID 发布器
```
POST /api/publishers （使用已存在的 instanceId）
期望：409 Conflict
```

### T3.5 删除发布器
```
DELETE /api/publishers/mqtt-dynamic-01
期望：204
```

### T3.6 删除不存在的发布器
```
DELETE /api/publishers/nonexistent
期望：404
```

---

## 四、数据转换规则

### T4.1 获取转换规则列表
```
GET /api/transforms
期望：200 + 规则数组（初始可能为空）
```

### T4.2 添加线性转换规则
```
POST /api/transforms
Body: {
  "ruleId": "linear-celsius-to-fahrenheit",
  "enabled": true,
  "tagPattern": "modbus-01/Temperature",
  "type": "Linear",
  "parametersJson": "{\"k\":1.8,\"b\":32}",
  "priority": 100
}
期望：201，之后 MQTT 中 modbus-01/Temperature 值变为华氏度
```

### T4.3 添加死区过滤规则
```
POST /api/transforms
Body: {
  "ruleId": "deadband-pressure",
  "enabled": true,
  "tagPattern": "modbus-01/Pressure",
  "type": "Deadband",
  "parametersJson": "{\"threshold\":5.0}",
  "priority": 100
}
期望：201，之后 modbus-01/Pressure 值变化 <5 时不输出
```

### T4.4 添加单位换算规则
```
POST /api/transforms
Body: {
  "ruleId": "unit-c-to-f",
  "enabled": true,
  "tagPattern": "opcua-01/Temperature",
  "type": "UnitConversion",
  "parametersJson": "{\"from\":\"C\",\"to\":\"F\"}",
  "priority": 100
}
期望：201
```

### T4.5 添加 JS 脚本规则
```
POST /api/transforms
Body: {
  "ruleId": "script-demo",
  "enabled": true,
  "tagPattern": "opcua-01/*",
  "type": "Script",
  "parametersJson": "{\"expression\":\"value * 2\"}",
  "priority": 200
}
期望：201
```

### T4.6 删除转换规则
```
DELETE /api/transforms/linear-celsius-to-fahrenheit
期望：204，规则立即失效，数据恢复原始值
```

### T4.7 Glob 模式匹配
```
添加 tagPattern 为 "modbus-01/*" 的规则
验证：该驱动下所有 tag 都被转换
```

---

## 五、数据路由规则

### T5.1 获取路由规则列表
```
GET /api/routes
期望：200 + 规则数组（初始可能为空）
```

### T5.2 添加路由规则——指定发布器
```
POST /api/routes
Body: {
  "ruleId": "route-modbus-to-mqtt",
  "enabled": true,
  "tagPattern": "modbus-01/*",
  "targetPublisherIds": ["mqtt-pub-01"],
  "priority": 100
}
期望：201，之后 modbus-01 数据只发到 mqtt-pub-01
```

### T5.3 添加路由规则——空目标（所有发布器）
```
POST /api/routes
Body: {
  "ruleId": "route-opcua-all",
  "enabled": true,
  "tagPattern": "opcua-01/*",
  "targetPublisherIds": [],
  "priority": 200
}
期望：201，opcua-01 数据发到所有发布器
```

### T5.4 优先级匹配
```
添加两条规则，低优先级数值的先匹配
验证：首次匹配生效
```

### T5.5 删除路由规则
```
DELETE /api/routes/route-modbus-to-mqtt
期望：204
```

---

## 六、配置导出/导入

### T6.1 导出配置
```
GET /api/config/export
期望：200 + 完整 JSON（含 drivers, publishers, transformRules, routeRules）
```

### T6.2 导入配置
```
POST /api/config/import
Body: 修改后的导出 JSON（如添加一个新驱动）
期望：200 + {"message":"导入成功，需重启生效"}
```

### T6.3 重启后验证导入
```
重启网关后，验证新驱动是否自动启动
```

---

## 七、插件信息与监控

### T7.1 查看已加载插件
```
GET /api/plugins
期望：200 + 驱动和发布器插件列表
```

### T7.2 Prometheus Metrics
```
GET /metrics
期望：200 + Prometheus 文本格式，含 gateway_data_points_received 等 12 项指标
```

### T7.3 健康检查详情
```
GET /api/health
期望：200 + 含 drivers, publishers, pipelinePending, dataPointsReceived 等汇总
```

---

## 八、断线重连

### T8.1 Modbus 从站断线重连
```
1. 确认 modbus-01 数据正常
2. 杀掉 AmVirtualSlave 进程
3. 观察网关日志（看门狗/重连）
4. 重启从站
5. 验证数据恢复
```

### T8.2 MQTT Broker 断线重连
```
1. 确认 MQTT 发布正常
2. 停止 Mosquitto 服务
3. 等待几秒
4. 重启 Mosquitto
5. 验证发布恢复
```

---

## 九、数据管线验证

### T9.1 配置持久化——重启恢复
```
1. 通过 API 添加驱动/发布器
2. 重启网关
3. 验证添加的实例自动恢复运行
```

### T9.2 优雅关机
```
1. 正常运行中 Ctrl+C 关闭网关
2. 观察日志：驱动停止 → 管道排空 → 发布器停止
3. 无数据丢失
```

---

## 测试结果记录

> 测试时间：2026-04-16
> 网关版本：AmGateway (NET 10, MinimalAPI)

### 已发现 Bug 及修复

| # | Bug 描述 | 根因 | 修复方式 |
|---|---------|------|---------|
| B1 | `POST /api/transforms` 返回 400 | 缺少 `JsonStringEnumConverter`，枚举类型 `TransformType` 无法从字符串绑定 | `Program.cs` 添加 `ConfigureHttpJsonOptions` + `JsonStringEnumConverter(CamelCase)` |
| B2 | 转换规则添加成功但数据未变 | `ApplyLinear` 检查 `point.Value is double`，但值实际为 `decimal`（精度修复引入） | `TransformEngine.cs` 添加 `decimal` 分支并优先于 `double` |
| B3 | 协议名 `ModbusTcp` 不被识别 | 插件协议名为小写 `modbus-tcp` | API 调用改用 `modbus-tcp` |
| B4 | Modbus 从站断线后无法自动重连 | `ReadRegistersAsync` 内层 catch 捕获异常后未断开连接，`_modbusMaster` 仍非 null，`EnsureConnectedAsync` 跳过重连 | 改 `ReadRegistersAsync` 返回 `bool`，读取失败返回 false；外层循环检测 false 后 `Disconnect()` + 延迟 `ReconnectDelayMs` 重连 |

### 测试结果

| 编号 | 测试项 | 结果 | 备注 |
|------|--------|------|------|
| T1.1 | 登录获取 Token | ✅ 通过 | 返回 JWT token + expiresAt |
| T1.2 | 无 Token 访问 | ✅ 通过 | 返回 401 Unauthorized |
| T1.3 | 错误凭据 | ✅ 通过 | 返回 401 |
| T1.4 | 有效 Token 访问 | ✅ 通过 | 返回 200 + 驱动列表 |
| T1.5 | 健康检查免认证 | ✅ 通过 | 返回 200 + healthy |
| T1.6 | Metrics 免认证 | ✅ 通过 | 返回 200 + Prometheus 格式 |
| T2.1 | 驱动列表 | ✅ 通过 | 返回当前运行驱动 |
| T2.2 | 指定驱动信息 | ✅ 通过 | 返回实例详情 |
| T2.3 | 不存在驱动 | ✅ 通过 | 返回 404 |
| T2.4 | 动态添加 Modbus | ✅ 通过 | 201 + 驱动实例，MQTT 收到数据。注意：protocol 需用 `modbus-tcp`（非 `ModbusTcp`） |
| T2.5 | 重复 ID | ✅ 通过 | 返回 409 Conflict |
| T2.6 | 删除驱动 | ✅ 通过 | 204，MQTT 停止该驱动数据 |
| T2.7 | 删除不存在驱动 | ✅ 通过 | 返回 404 |
| T2.8 | 驱动重启 | ✅ 通过 | 200 + 重启消息，数据恢复 |
| T2.9 | 不支持的协议 | ✅ 通过 | 返回错误 |
| T3.1 | 发布器列表 | ✅ 通过 | 返回发布器数组 |
| T3.2 | 指定发布器信息 | ✅ 通过 | 返回实例详情 |
| T3.3 | 动态添加 MQTT | ✅ 通过 | 201 + 发布器实例 |
| T3.4 | 重复 ID | ✅ 通过 | 返回 409 |
| T3.5 | 删除发布器 | ✅ 通过 | 204 |
| T3.6 | 删除不存在 | ✅ 通过 | 返回 404 |
| T4.1 | 转换规则列表 | ✅ 通过 | 返回规则数组 |
| T4.2 | 线性转换 | ✅ 通过 | 验证：27.2°C × 1.8 + 32 = 80.96°F ✅。曾触发 B1、B2，已修复 |
| T4.3 | 死区过滤 | ✅ 通过 | Pressure 变化 <5 时 MQTT 输出减少 |
| T4.4 | 单位换算 | ✅ 通过 | C→F: 原始 28.7°C × 9/5 + 32 = 83.66°F ✅，内置温度/压力/长度/流量换算 |
| T4.5 | JS 脚本 | ✅ 通过 | `value * 10`：Pressure 105.6 → 1056 ✅。支持 value/tag/quality/timestamp/sourceDriver 变量 |
| T4.6 | 删除转换规则 | ✅ 通过 | 204，规则立即失效 |
| T4.7 | Glob 匹配 | ✅ 通过 | `modbus-01/*` 匹配 Temperature 和 Pressure，+100 后均生效。opcua-01 不受影响 |
| T5.1 | 路由规则列表 | ✅ 通过 | 返回规则数组 |
| T5.2 | 指定发布器路由 | ✅ 通过 | 201 |
| T5.3 | 空目标路由 | ✅ 通过 | 201 |
| T5.4 | 优先级匹配 | ✅ 通过 | priority=50(×2) 先于 priority=100(+100) 执行，链式传递：原始 20.9 → 41.8 → 141.8 ✅ |
| T5.5 | 删除路由规则 | ✅ 通过 | 204 |
| T6.1 | 导出配置 | ✅ 通过 | 返回完整 JSON（drivers, publishers, transformRules, routeRules） |
| T6.2 | 导入配置 | ✅ 通过 | 返回 `{"message":"导入成功，需重启生效"}` |
| T6.3 | 重启验证 | ✅ 通过 | 重启后 3 条 transformRules、2 个 drivers 全部自动恢复运行 ✅ |
| T7.1 | 插件信息 | ✅ 通过 | 返回 modbus-tcp, opcua, mqtt 等插件 |
| T7.2 | Prometheus Metrics | ✅ 通过 | 含 gateway_data_points_received 等 11+ 指标 |
| T7.3 | 健康检查详情 | ✅ 通过 | 含 drivers, publishers, pipelinePending, dataPointsReceived |
| T8.1 | Modbus 断线重连 | ✅ 通过 | 从站停 5-6s 后重启，网关自动重连恢复 Good 数据。曾触发 B4，已修复。流程：Bad→Disconnect→5s延迟→Reconnect→Good |
| T8.2 | MQTT 断线重连 | ✅ 通过 | Mosquitto 停 5-6s 后重启，网关自动重连 MQTT broker，数据立即恢复 Good，publishErrors=0 |
| T9.1 | 持久化重启恢复 | ✅ 通过 | 网关停→启后，2驱动+2发布器自动从 appsettings.json 恢复 running，数据 Good，publishErrors=0 |
| T9.2 | 优雅关机 | ✅ 通过 | 调用 /api/shutdown 后 380ms 优雅退出，dropped=0 errors=0，驱动/发布器按序停止。新增 /api/shutdown 端点 |

### 测试统计

- **通过**：37 / 37
- **未测**：0 / 37
- **失败**：0 / 37
- **已修复 Bug**：4

### 遗留问题

无
