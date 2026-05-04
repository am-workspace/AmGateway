# 网关指标采集器（GatewayMetrics）待办事项

> 当前完成度约 80%，核心功能已闭环，以下为需要改进的问题。

---

## 高优先级

### 1. 延迟分位数改用 histogram 规范输出

**现状：** P50/P95/P99 使用 `gauge` 类型输出，不符合 Prometheus 规范。

**问题：** Prometheus 无法使用 `histogram_quantile()` 函数查询，Grafana 无法正确聚合多实例分位数。

**目标格式：**

```text
# TYPE amgateway_pipeline_latency_ms histogram
amgateway_pipeline_latency_ms_bucket{le="5"} 10
amgateway_pipeline_latency_ms_bucket{le="10"} 25
amgateway_pipeline_latency_ms_bucket{le="50"} 40
amgateway_pipeline_latency_ms_bucket{le="+Inf"} 42
amgateway_pipeline_latency_ms_sum 580.5
amgateway_pipeline_latency_ms_count 42
```

**改动点：** `ExportPrometheus()` 方法中延迟统计部分，改用预定义分桶 + `WriteHistogram` 方法。

---

### 2. 计数器按 driver/publisher 添加 label 维度

**现状：** 所有计数器（Received/Published/Dropped 等）都是全局汇总值，无拆分维度。

**问题：** 工业场景下无法定位是哪个驱动或发布器出了问题，Prometheus 按 label 拆分查询的核心能力未利用。

**目标格式：**

```text
amgateway_data_points_received_total{driver="modbus_tcp"} 500
amgateway_data_points_received_total{driver="opcua"} 300
amgateway_publish_errors_total{publisher="mqtt_broker_1"} 12
```

**改动点：** 计数器方法需要增加 label 参数（如 `DataPointReceived(string driverId)`），内部存储改为 `ConcurrentDictionary<string, long>` 或类似结构。

---

## 中优先级

### 3. 增加进程级基础指标

**现状：** 缺少进程/运行时级别的监控指标。

**问题：** 无法发现内存泄漏、GC 暂停、线程饥饿等问题，工业驱动常驻连接场景下句柄泄漏也难发现。

**建议增加的指标：**

| 指标 | 来源 | 作用 |
|------|------|------|
| `process_memory_working_set_bytes` | `Process.GetCurrentProcess()` | 发现内存泄漏 |
| `process_cpu_seconds_total` | `Process.TotalProcessorTime` | CPU 使用率 |
| `dotnet_gc_pause_seconds_total` | `EventCounters` | GC 暂停影响 Pipeline 延迟 |
| `dotnet_threadpool_queue_length` | `ThreadPool.PendingWorkItemCount` | 线程饥饿判断 |
| `process_open_handles` | `Process.HandleCount` | 连接泄漏检测 |

**改动点：** `ExportPrometheus()` 中追加进程指标输出，可考虑引入 `System.Diagnostics.Metrics` 或 `EventCounters` 监听。

---

### 4. ChannelCapacity 与实际 Channel 容量同步

**现状：** `_channelCapacity` 硬编码默认值 10000，而实际 `Channel` 容量在 `ChannelDataPipeline` 构造时指定。

**问题：** 配置变更后指标输出与实际容量不一致，误导运维。

**改动点：** `ChannelDataPipeline` 构造时主动调用 `SetChannelCapacity()` 设置实际值，移除 `GatewayMetrics` 中的硬编码默认值。

---

## 低优先级

### 5. 吞吐量改为滑动窗口速率

**现状：** 吞吐量计算为 `_dataPointsReceived / uptime`，是全生命周期平均值。

**问题：** 运行时间越长，该值越迟钝，无法反映近期突发流量。

**建议：** 增加最近 60s 的滑动窗口速率指标（或依赖 Prometheus `rate()` 函数计算，前提是 counter 类型正确）。

**改动点：** 可选 — 增加 `ConcurrentQueue<(DateTimeOffset timestamp, long count)>` 按秒采样，或直接依赖 Prometheus 端 `rate()` 函数。

---

### 6. RecordLatency 竞态窗口

**现状：** `Enqueue` 和 `TryDequeue` 之间可能短暂超过 1000 条上限。

```csharp
_recentLatencyMs.Enqueue(elapsed.TotalMilliseconds);
while (_recentLatencyMs.Count > MaxLatencySamples)  // Count 可能已超过
    _recentLatencyMs.TryDequeue(out _);
```

**问题：** 高并发下队列可能短暂多出几条，对监控精度影响极小。

**改动点：** 用 `Interlocked` 计数代替 `Count` 属性判断，或保持现状（影响可忽略）。

---

### 7. GetSnapshot() 读取 int 字段与 Interlocked 写入风格不一致

**现状：** 写入用 `Interlocked.Exchange`，读取直接访问字段。

```csharp
// 写入
Interlocked.Exchange(ref _channelCount, count);
// 读取
["channelItems"] = _channelCount,  // 未用 Interlocked
```

**问题：** 64 位系统上 `int` 读取本身是原子的，但风格与计数器读取不一致，可能造成误解。

**改动点：** 统一使用 `Interlocked.CompareExchange(ref _channelCount, 0, 0)` 读取，或将 gauge 字段改为 `volatile` 明确语义。
