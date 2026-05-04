# RouteResolver 待办清单

> 审查文件: `AmGateway/Pipeline/RouteResolver.cs`
> 审查时间: 2026-05-03

---

## 🔴 严重问题（必须修复）

### 1. `Resolve` 中重复执行 `OrderBy`
- **问题**: `LoadRules` 第73行已将 `_rules` 按 `Priority` 排序，但 `Resolve` 第45行又执行了一次 `OrderBy(r => r.Priority)`。`_rules` 引用在加载后不会变动，此排序完全多余。
- **影响**: 高规则量、高数据吞吐场景下，每条数据点都产生一次 O(N log N) 的排序开销，CPU 占用显著升高。
- **建议**: 移除 `Resolve` 中的 `OrderBy`，直接 `Where(r => r.Enabled).FirstOrDefault(r => IsTagMatch(...))`，利用 `LoadRules` 时的预排序保证优先级顺序。

### 2. `IsTagMatch` 每次调用都编译正则表达式
- **问题**: 每条数据点进入时都要执行 `Regex.Escape(pattern).Replace(...)` 和 `Regex.IsMatch(...)`。规则使用了 `*` 或 `?` 时，此路径被高频触发，产生大量临时字符串和正则编译开销。
- **建议**: 在 `LoadRules` 或 `RouteRule` 初始化时预编译 `Regex` 对象并缓存；精确匹配和 `*`/`**` 通配符走短路逻辑，避免创建正则对象。

### 3. 路由语义是"只命中第一条匹配规则"，可能不符合用户预期
- **问题**: `FirstOrDefault(r => IsTagMatch(...))` 只取优先级最高的**第一条**匹配规则，其余匹配规则被静默忽略。如果用户配置了两条规则（规则A发到MQTT，规则B发到InfluxDB），当前实现只会执行规则A，规则B完全失效。
- **建议**:
  - 方案A：明确文档化此行为，在配置导入/API时给出警告提示。
  - 方案B：改为收集**所有**匹配规则的目标发布器并集，让路由更灵活（一个数据点可同时路由到多个发布器组合）。

---

## 🟡 中等问题（建议修复）

### 4. 每次 `Resolve` 都创建新的 `HashSet`
- **问题**: `new HashSet<string>(matchedRule.TargetPublisherIds, StringComparer.OrdinalIgnoreCase)` 在每条数据点经过时都重建。`TargetPublisherIds` 在规则生命周期内不变，属于不必要的重复分配。
- **建议**: 在 `LoadRules` 时将 `TargetPublisherIds` 预编译为 `HashSet<string>` 缓存到规则条目中，运行时直接复用。

### 5. 缺少路由命中/未命中的调试日志
- **问题**: `Resolve` 方法全程无日志输出。当数据点被错误路由或规则未按预期生效时，排查非常困难。
- **建议**: 增加不同级别的日志：
  - `LogDebug`: Tag命中规则时输出规则ID和目标发布器列表
  - `LogDebug`: Tag未命中任何规则时输出（默认路由到所有发布器）
  - `LogTrace`: 每次 `IsTagMatch` 的匹配过程（调试复杂通配符时有用）

### 6. `allPublishers.Where(...).ToList()` 每次分配新列表
- **问题**: 匹配到规则时，通过 LINQ `Where` + `ToList()` 产生新的 `List<IPublisher>` 实例。高频数据点场景下频繁分配小对象，增加 GC 压力。
- **建议**: 若 `IReadOnlyList<IPublisher>` 语义允许，可预计算常见发布器组合的数组并缓存；或考虑返回 `IPublisher[]` 而非 `List<T>`。

---

## 🟢 性能优化（吞吐提升）

### 7. 考虑按 Tag 前缀或 SourceDriver 建立索引
- **问题**: 当前是线性扫描 `_rules` 列表，每条数据点都要逐个调用 `IsTagMatch`。规则量增大到几百条时，此处会成为显著瓶颈。
- **建议**:
  - 精确匹配规则（无 `*`/`?`）放入 `Dictionary<string, RouteRule>`，O(1) 查找。
  - 通配符规则单独存放一个列表，作为 fallback 扫描。
  - 可按 `SourceDriver` 或 `TagPrefix` 做二级索引，进一步缩小候选集。

### 8. 支持规则的增删改热更新
- **问题**: `LoadRules` 是全量替换，调用期间需要 lock。无法实现"只改一条规则"的轻量级热更新。
- **建议**: 考虑支持增量更新接口（`AddRule`/`RemoveRule`/`UpdateRule`），使用 `ReaderWriterLockSlim` 替代 `lock`，减少读操作（`Resolve`）的锁竞争。

---

## 📎 补充建议

- **路由环回检测**: 如果后续支持发布器将数据写回通道（如双向控制），需要考虑路由规则是否会导致数据环回（死循环）。
- **规则执行耗时监控**: 对 `Resolve` 增加耗时统计，便于识别因复杂通配符规则导致的慢路由。
- **空 `TargetPublisherIds` 语义文档化**: 当前空列表=路由到所有发布器，这个行为需要明确写入文档，避免用户误以为空列表=丢弃数据。
