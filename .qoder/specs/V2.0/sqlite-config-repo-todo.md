# SqliteConfigRepository 待办清单

> 审查文件: `AmGateway/Services/SqliteConfigRepository.cs`
> 审查时间: 2026-05-03

---

## 🔴 严重问题（必须修复）

### 1. 每个操作都新建连接，无连接复用
- **问题**: 每条 SQL 都执行 `new SqliteConnection(_connectionString)` + `OpenAsync()`。SQLite 是文件级锁，频繁开关在高并发下性能差，且竞争文件句柄。即使 `Microsoft.Data.Sqlite` 有内部连接池，每次 new 仍是开销。
- **影响**: 高并发场景下连接建立开销明显，且可能出现锁竞争异常。
- **建议**: 考虑使用 `DbContext` 或保持单例长连接；若保持当前模式，至少用一个共享连接池实例。可封装一个 `GetConnection()` 工厂方法，便于后续切换为连接池。

### 2. `ImportFromJsonAsync` 无事务包裹，且逐条保存
- **问题**: 导入 100 条规则 = 100 次独立 INSERT/UPDATE。中间调用 `Save*Async`，而 `Save*Async` 内部每次都要 `OpenAsync` + 执行 + 关闭。中途失败时数据库处于**半完成状态**（部分规则导入成功，部分失败），数据不一致。
- **影响**: 大批量导入极慢；失败时回滚困难，需手动清理脏数据。
- **建议**: `ImportFromJsonAsync` 应开启显式事务（`BEGIN TRANSACTION`），批量执行后 `COMMIT`。可使用同一个连接对象复用，避免重复开关。

### 3. `_initialized` 不是线程安全的
- **问题**: `EnsureInitializedAsync` 中 `if (_initialized) return;` 无锁保护。两个线程同时进入时，都会执行建表语句。虽然 `CREATE TABLE IF NOT EXISTS` 是幂等的，但会浪费资源，且极端情况下可能抛锁冲突异常。
- **建议**: 加 `lock` 或使用 `Interlocked` / `Lazy<Task>` 确保只执行一次。

### 4. `ExportToJsonAsync` 做了不必要的反序列化-再序列化
- **问题**: `Settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(d.SettingsJson)` 先把 JSON 字符串反序列化为 `Dictionary`，再整体 `Serialize(export)`。数据在内存中兜了一圈，增加 GC 压力。
- **建议**: 使用 `JsonNode.Parse()` 或 `JsonElement` 透传原始 JSON，避免中间对象分配。

---

## 🟡 中等问题（建议修复）

### 5. 所有数据库操作缺乏异常处理
- **问题**: 任何 SQL 异常（如磁盘满、文件被占用、权限不足）会直接抛给调用方。没有日志记录具体的 SQL 失败原因，也没有回滚机制。
- **建议**: 在外层加 try/catch，记录 `SqliteException` 的错误码（`SqliteErrorCode`）和具体 SQL 语句，方便排查。

### 6. `DateTimeOffset.Parse` 格式假设不健壮
- **问题**: `CreatedAt = DateTimeOffset.Parse(reader.GetString(4))` 假设数据库中的字符串**一定**是有效的 ISO 8601。如果手动修改数据库或旧版本数据格式不同，这里会直接崩溃。
- **建议**: 使用 `DateTimeOffset.TryParse` 降级处理；或 SQLite 中使用 `datetime` 类型，通过 `reader.GetDateTime()` 读取。

### 7. `ImportFromJsonAsync` 中 `Enum.Parse` 无保护
- **问题**: `Type = Enum.Parse<TransformType>(typeStr, true)` 如果 JSON 中 `Type` 字段拼写错误（如 `"Linea"`），直接抛 `ArgumentException`，中断整个导入流程。
- **建议**: 使用 `Enum.TryParse`，解析失败时记录警告日志并跳过该条规则，让其余规则正常导入。

### 8. 删除操作无关联数据清理
- **问题**: 删除驱动或发布器时，只删除 `drivers` / `publishers` 表记录。如果路由规则中引用了该发布器 ID，或转换规则引用了相关 Tag，会留下**悬空引用**。
- **建议**: 考虑级联清理或添加引用完整性检查（SQLite 外键约束），在删除前提示用户关联规则情况。

---

## 🟢 性能优化（吞吐提升）

### 9. 批量导入性能
- **问题**: 逐条调用 `Save*Async`，每条独立 SQL 语句。大量数据导入时性能极差。
- **建议**:
  - 使用 `BEGIN TRANSACTION; ...; COMMIT;` 包裹批量操作。
  - 或构造 `INSERT INTO ... VALUES (...), (...), (...)` 批量插入语句。
  - 导入时复用同一个 `SqliteConnection`，避免重复开关。

### 10. 增加配置版本控制
- **问题**: 导出 JSON 中没有 `version` 字段，将来 schema 变更后难以兼容旧备份文件。
- **建议**: 导出时增加 `"version": "1.0"` 字段；导入时校验版本号，版本不匹配时给出明确提示或执行迁移逻辑。

### 11. 考虑读写分离锁
- **问题**: 导入/导出和运行时读取并发时，SQLite 会串行化（文件级锁），阻塞运行时的配置读取。
- **建议**: 使用 `ReaderWriterLockSlim` 控制：允许多个读并发，写时独占。或考虑在内存中维护配置缓存，SQLite 仅作为持久化层，运行时读缓存不读库。

---

## 📎 补充建议

- **索引优化**: `transform_rules` 和 `route_rules` 按 `priority` 排序读取频繁，可考虑在 `priority` 字段上加索引。
- **连接字符串参数**: 当前连接字符串只有 `Data Source=...`，建议增加 `Cache=Shared` 和 `Pooling=true`（若版本支持），提升并发性能。
- **SQL 注入风险**: 当前使用参数化查询（`@id` 等），这一点是正确的。继续保持，不要改为字符串拼接 SQL。
