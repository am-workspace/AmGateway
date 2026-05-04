# 插件管理器（PluginManager）待办事项

> 核心功能已实现（发现、加载、创建、卸载），以下为需要改进的问题。

---

## 高优先级

### 1. DLL 搜索不递归，当前目录结构下找不到插件

**现状：** `LoadPluginFromDirectory` 使用 `Directory.GetFiles(pluginDir, "AmGateway.*.dll")` 只搜索一层目录。

**问题：** 实际插件 DLL 位于 `bin/Debug/net8.0/` 子目录中，当前搜索逻辑找不到，导致启动时插件加载失败。

**改动点：**

```csharp
// 改前：只搜一层
Directory.GetFiles(pluginDir, "AmGateway.*.dll")

// 改后：递归搜所有子目录
Directory.GetFiles(pluginDir, "AmGateway.*.dll", SearchOption.AllDirectories)
```

---

### 2. 同一 DLL 的 Driver 和 Publisher 共享 LoadContext，卸载有风险

**现状：** 一个 DLL 中如果同时包含 Driver 和 Publisher，它们注册时共用同一个 `PluginLoadContext`。

**问题：** 调用 `UnloadPlugin("modbus-tcp")` 会卸载整个 LoadContext，如果同一 DLL 中还有 Publisher，Publisher 的类型也跟着失效，后续调用会崩溃。`UnloadAll()` 中的注释也承认了此问题。

**改动点：** 按 DLL 路径管理 LoadContext（如 `Dictionary<string, PluginLoadContext>`），只有所有使用该 DLL 的 Driver/Publisher 都停止后才卸载对应的 LoadContext。

---

### 3. Activator.CreateInstance 要求无参构造函数，但接口无约束

**现状：** `CreateDriverInstance` / `CreatePublisherInstance` 使用 `Activator.CreateInstance(type)` 调用无参构造。

**问题：** 如果插件开发者写了带参数的构造函数，运行时直接报错且没有编译期检查，错误信息也不直观。

**改动点：**
- 在接口文档或 `DriverMetadataAttribute` / `PublisherMetadataAttribute` 中明确要求必须提供无参构造函数
- 或改用 `ActivatorUtilities.CreateInstance(sp, type)` 支持 DI 注入，提升灵活性

---

## 中优先级

### 4. LoadContext.Unload() 非立即生效，DLL 文件仍被锁定

**现状：** 加载失败时调用 `loadContext.Unload()` 尝试卸载，但 `Unload()` 只是请求卸载，需等 GC 回收所有引用后才真正生效。

**问题：** 短期内 DLL 文件仍被锁定，无法替换。对于需要热替换插件的场景（如运维更新插件），会导致"文件被占用"错误。

**改动点：** 记录失败加载的 LoadContext，在下次 GC 后确认卸载，或提供重试机制。可考虑在卸载后强制 `GC.Collect()` + `GC.WaitForPendingFinalizers()`。

---

### 5. Abstractions 版本兼容性无检查

**现状：** `PluginLoadContext.IsSharedAssembly` 对 `AmGateway.Abstractions` 直接回退到主程序版本。

**问题：** 如果插件编译时用的 Abstractions 版本和主程序不同（尤其是接口有破坏性变更），运行时会抛 `MissingMethodException` / `TypeLoadException` 等诡异错误，难以定位根因。

**改动点：** 加载插件时检查 Abstractions 的 `Assembly.GetName().Version`，与主程序版本对比，不兼容时给出明确错误提示而非运行时才炸。

---

### 6. GetLoadedDriverPlugins() 返回内部字典引用，有线程安全隐患

**现状：**

```csharp
public IReadOnlyDictionary<string, PluginInfo> GetLoadedDriverPlugins() => _driverPlugins;
```

**问题：** 返回的是原始字典的只读视图，调用方可通过 `PluginInfo` 的可变属性间接影响内部状态。如果其他线程同时修改字典，还可能抛 `InvalidOperationException`。

**改动点：** 返回快照副本 `.ToDictionary()` 或将内部字典改为 `ConcurrentDictionary`。

---

## 低优先级

### 7. 不支持运行时热加载新插件

**现状：** `DiscoverPlugins()` 只在启动时调用一次。

**问题：** 运行时往 plugins 目录放入新 DLL 无法被发现，必须重启网关。

**改动点：** 增加 `ReloadPlugin(string protocolName)` 方法，或用 `FileSystemWatcher` 监听目录变化自动发现新插件。

---

### 8. 缺少插件依赖完整性校验

**现状：** `AssemblyDependencyResolver` 会尝试解析依赖，但如果插件依赖的第三方库缺失（没一起放进插件目录），只会在 `CreateInstance` 时才报错。

**问题：** 问题暴露太晚，错误信息不直观，难以判断是"插件本身有 bug"还是"依赖缺失"。

**改动点：** `LoadPluginFromDirectory` 之后主动校验依赖完整性（如检查插件引用的所有程序集是否可解析），提前暴露问题并给出清晰提示。
