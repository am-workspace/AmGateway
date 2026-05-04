# TransformEngine 待办清单

> 审查文件: `AmGateway/Pipeline/TransformEngine.cs`
> 审查时间: 2026-04-30

---

## 🔴 严重问题（必须修复）

### 1. Jint 引擎线程不安全，存在数据串扰风险
- **问题**: `TransformRuleEntry` 为每条规则只缓存一个 `Engine` 实例，`ApplyScript` 中反复 `SetValue` + `Evaluate`。Jint 的 `Engine` **不是线程安全的**。若后续管道并行化，多个数据点同时命中同一条 Script 规则时，`SetValue("value", ...)` 会互相覆盖，导致脚本读到错误的值。
- **建议**: 使用 `ThreadLocal<Engine>` 或对象池，确保每个线程拥有独立的引擎实例；或在 `ApplyScript` 时 lock 住 `entry`（但会串行化脚本执行）。

### 2. Script 引擎未限制危险 API，存在安全风险
- **问题**: `new Engine(options => { MaxStatements(10000); TimeoutInterval(...); })` 未禁用文件系统、网络、反射等访问。恶意脚本理论上可执行 `System.IO.File.ReadAllText(...)` 或通过 .NET interop 进行危险操作。
- **建议**: 配置 `options.Strict()`；通过 `options.SetTypeResolver` 或白名单机制，仅开放 `Math` 等安全类型；考虑禁用 `clr` 访问。

### 3. `_lastValues` 存在内存泄漏风险
- **问题**: 死区过滤字典 key 为 `point.Tag`。若驱动产生动态变化的 Tag（如包含序列号、GUID、时间戳），`_lastValues` 会无限膨胀，且无任何淘汰机制。
- **建议**: 添加 LRU 淘汰策略（如 `ConcurrentLru` 或 `MemoryCache`），或限制字典最大容量，超出时清理最旧条目。

### 4. `IsTagMatch` 每次调用都编译正则表达式
- **问题**: `Regex.Escape(pattern).Replace(...)` + `Regex.IsMatch(...)` 在每条数据点进入时都要执行一次字符串替换和正则编译。规则带通配符且数据吞吐高时，产生大量临时对象和 CPU 开销。
- **建议**: 在 `CreateEntry` 时预编译 `Regex` 对象，存到 `TransformRuleEntry` 中复用；精确匹配和 `*` 通配符走短路逻辑，避免创建正则。

---

## 🟡 中等问题（建议修复）

### 5. 分段线性 `Points` 每次调用都排序
- **问题**: `ApplyPiecewiseLinear` 中每次对 `points` 执行 `Sort()`，而配置数据是不变的。
- **建议**: 在 `CreateEntry` 时预解析、预排序并缓存到 `TransformRuleEntry` 中。

### 6. 死区状态不持久化，重启后丢失
- **问题**: `_lastValues` 是纯内存字典，网关重启后所有 Tag 死区历史归零。对于高频抖动信号，重启瞬间会产生一波无效数据穿透。
- **建议**: 可选地支持将死区状态持久化到 SQLite，或从上次已知值恢复。

### 7. `UnitConverter` 缺少反向自动推导
- **问题**: 只注册了单向转换（如 `C→F`），未注册反向（`F→C`）时直接返回 `null`。
- **建议**: 注册单向转换时，自动推导并注册其逆运算函数（`f⁻¹`），减少配置量。

### 8. `ApplyLinear` 遗漏多种数值类型
- **问题**: 只处理了 `decimal/double/float/int/long/short/ushort/byte`，遗漏了 `uint`、`ulong`、`sbyte`、`nuint` 等。这些类型走到最后的 `LogWarning`，规则实际未生效。
- **建议**: 统一使用 `Convert.ToDouble()` 做输入转换，输出时根据原始类型选择回写策略；或至少补充遗漏的常见类型。

---

## 🟢 性能优化（吞吐提升）

### 9. 规则匹配全表扫描 + 运行时排序
- **问题**: 每次 `Transform` 都遍历全部规则、Glob 匹配、再按 `Priority` 排序。规则量大且数据点吞吐高时，此处会成为瓶颈。
- **建议**:
  - 精确匹配规则走 `Dictionary<string, List<TransformRuleEntry>>` 索引，O(1) 定位。
  - 通配符规则预按 Priority 排序，避免运行时 `OrderBy`。
  - 考虑按 `SourceDriver` 或 `TagPrefix` 做二级索引，减少候选集。

### 10. Script 脚本返回值类型单一
- **问题**: 当前只支持返回 `bool/number/string`，无法同时修改 `Quality`、`Timestamp` 等字段。
- **建议**: 支持返回对象，如 `{ value: 123, quality: "Good", drop: false }`，让脚本具备修改多字段的能力。

---

## 📎 补充建议

- **规则热重载**: `LoadRules` 是全量替换，可以考虑支持增量更新（增删改单条规则），避免短暂的服务中断。
- **规则执行耗时监控**: 对 `ApplyRule` 增加耗时统计（尤其是 Script 类型），便于在日志中识别拖慢整体吞吐的慢规则。
- `TransformRuleEntry.GetOrCreateEngine()` 的 `TimeoutInterval` 设为 5 秒，建议同时限制最大内存占用（`options.LimitRecursion`、`options.MaxArraySize` 等），防止脚本耗尽进程资源。
