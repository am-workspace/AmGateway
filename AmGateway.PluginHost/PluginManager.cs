using System.Reflection;
using AmGateway.Abstractions;
using AmGateway.Abstractions.Metadata;
using Microsoft.Extensions.Logging;

namespace AmGateway.PluginHost;

/// <summary>
/// 插件管理器 - 负责发现、加载、创建和卸载协议驱动和北向发布器插件
/// </summary>
public sealed class PluginManager
{
    private readonly ILogger<PluginManager> _logger;
    private readonly Dictionary<string, PluginInfo> _driverPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginInfo> _publisherPlugins = new(StringComparer.OrdinalIgnoreCase);

    public PluginManager(ILogger<PluginManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 扫描插件目录，发现并加载所有协议驱动和北向发布器插件
    /// </summary>
    public void DiscoverPlugins(string pluginsRootPath)
    {
        var fullPath = Path.GetFullPath(pluginsRootPath);

        if (!Directory.Exists(fullPath))
        {
            _logger.LogWarning("[PluginManager] 插件目录不存在: {Path}，跳过插件发现", fullPath);
            return;
        }

        var pluginDirs = Directory.GetDirectories(fullPath);
        _logger.LogInformation("[PluginManager] 扫描插件目录: {Path}，发现 {Count} 个子目录", fullPath, pluginDirs.Length);

        foreach (var dir in pluginDirs)
        {
            try
            {
                LoadPluginFromDirectory(dir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PluginManager] 加载插件失败: {Dir}", dir);
            }
        }

        _logger.LogInformation("[PluginManager] 插件发现完成: {DriverCount} 个驱动, {PublisherCount} 个发布器",
            _driverPlugins.Count, _publisherPlugins.Count);
    }

    /// <summary>
    /// 创建指定协议的驱动实例
    /// </summary>
    public IProtocolDriver? CreateDriverInstance(string protocolName)
    {
        if (!_driverPlugins.TryGetValue(protocolName, out var pluginInfo))
        {
            _logger.LogError("[PluginManager] 未找到协议 '{Protocol}' 的驱动插件", protocolName);
            return null;
        }

        if (pluginInfo.DriverType == null)
        {
            _logger.LogError("[PluginManager] 插件 '{Protocol}' 不包含驱动实现", protocolName);
            return null;
        }

        try
        {
            var instance = Activator.CreateInstance(pluginInfo.DriverType) as IProtocolDriver;
            if (instance == null)
            {
                _logger.LogError("[PluginManager] 创建驱动实例失败: 类型 {Type} 无法转换为 IProtocolDriver", pluginInfo.DriverType.FullName);
                return null;
            }

            _logger.LogInformation("[PluginManager] 已创建驱动实例: {Protocol} ({Type})",
                protocolName, pluginInfo.DriverType.FullName);
            return instance;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PluginManager] 创建驱动实例异常: {Protocol}", protocolName);
            return null;
        }
    }

    /// <summary>
    /// 创建指定传输的发布器实例
    /// </summary>
    public IPublisher? CreatePublisherInstance(string transportName)
    {
        if (!_publisherPlugins.TryGetValue(transportName, out var pluginInfo))
        {
            _logger.LogError("[PluginManager] 未找到传输 '{Transport}' 的发布器插件", transportName);
            return null;
        }

        if (pluginInfo.PublisherType == null)
        {
            _logger.LogError("[PluginManager] 插件 '{Transport}' 不包含发布器实现", transportName);
            return null;
        }

        try
        {
            var instance = Activator.CreateInstance(pluginInfo.PublisherType) as IPublisher;
            if (instance == null)
            {
                _logger.LogError("[PluginManager] 创建发布器实例失败: 类型 {Type} 无法转换为 IPublisher", pluginInfo.PublisherType.FullName);
                return null;
            }

            _logger.LogInformation("[PluginManager] 已创建发布器实例: {Transport} ({Type})",
                transportName, pluginInfo.PublisherType.FullName);
            return instance;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PluginManager] 创建发布器实例异常: {Transport}", transportName);
            return null;
        }
    }

    /// <summary>
    /// 卸载指定协议的驱动插件
    /// </summary>
    public void UnloadPlugin(string protocolName)
    {
        if (_driverPlugins.TryGetValue(protocolName, out var pluginInfo))
        {
            pluginInfo.LoadContext.Unload();
            _driverPlugins.Remove(protocolName);
            _logger.LogInformation("[PluginManager] 已卸载驱动插件: {Protocol}", protocolName);
        }
    }

    /// <summary>
    /// 卸载所有插件
    /// </summary>
    public void UnloadAll()
    {
        foreach (var kvp in _driverPlugins)
        {
            try
            {
                kvp.Value.LoadContext.Unload();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PluginManager] 卸载插件 {Protocol} 时出错", kvp.Key);
            }
        }

        // publisher 插件可能和 driver 共享 LoadContext，Unload 会忽略已卸载的
        foreach (var kvp in _publisherPlugins)
        {
            try
            {
                kvp.Value.LoadContext.Unload();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PluginManager] 卸载发布器插件 {Transport} 时出错", kvp.Key);
            }
        }

        _driverPlugins.Clear();
        _publisherPlugins.Clear();
        _logger.LogInformation("[PluginManager] 所有插件已卸载");
    }

    /// <summary>
    /// 获取所有已加载的驱动插件信息
    /// </summary>
    public IReadOnlyDictionary<string, PluginInfo> GetLoadedDriverPlugins() => _driverPlugins;

    /// <summary>
    /// 获取所有已加载的发布器插件信息
    /// </summary>
    public IReadOnlyDictionary<string, PluginInfo> GetLoadedPublisherPlugins() => _publisherPlugins;

    /// <summary>
    /// 从目录加载单个插件
    /// </summary>
    private void LoadPluginFromDirectory(string pluginDir)
    {
        var dirName = Path.GetFileName(pluginDir);

        // 查找所有 AmGateway.*.dll（Driver 和 Publisher 都匹配）
        var pluginDlls = Directory.GetFiles(pluginDir, "AmGateway.*.dll")
            .Where(f => !f.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("AmGateway.Abstractions", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (pluginDlls.Length == 0)
        {
            _logger.LogDebug("[PluginManager] 目录 {Dir} 中未找到插件 DLL，跳过", dirName);
            return;
        }

        foreach (var dllPath in pluginDlls)
        {
            var loadContext = new PluginLoadContext(dllPath);

            try
            {
                var assembly = loadContext.LoadFromAssemblyPath(dllPath);
                var driverTypes = FindDriverTypes(assembly);
                var publisherTypes = FindPublisherTypes(assembly);

                // 注册驱动插件
                foreach (var (driverType, metadata) in driverTypes)
                {
                    if (_driverPlugins.ContainsKey(metadata.ProtocolName))
                    {
                        _logger.LogWarning("[PluginManager] 协议 '{Protocol}' 已存在，跳过重复驱动: {Dll}",
                            metadata.ProtocolName, Path.GetFileName(dllPath));
                        continue;
                    }

                    _driverPlugins[metadata.ProtocolName] = new PluginInfo
                    {
                        PluginDirectory = pluginDir,
                        LoadContext = loadContext,
                        DriverType = driverType,
                        DriverMetadata = metadata
                    };

                    _logger.LogInformation("[PluginManager] 已发现驱动插件: {Name} ({Protocol}) v{Version}",
                        metadata.Name, metadata.ProtocolName, metadata.Version);
                }

                // 注册发布器插件
                foreach (var (publisherType, metadata) in publisherTypes)
                {
                    if (_publisherPlugins.ContainsKey(metadata.TransportName))
                    {
                        _logger.LogWarning("[PluginManager] 传输 '{Transport}' 已存在，跳过重复发布器: {Dll}",
                            metadata.TransportName, Path.GetFileName(dllPath));
                        continue;
                    }

                    _publisherPlugins[metadata.TransportName] = new PluginInfo
                    {
                        PluginDirectory = pluginDir,
                        LoadContext = loadContext,
                        PublisherType = publisherType,
                        PublisherMetadata = metadata
                    };

                    _logger.LogInformation("[PluginManager] 已发现发布器插件: {Name} ({Transport}) v{Version}",
                        metadata.Name, metadata.TransportName, metadata.Version);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PluginManager] 加载程序集失败: {Dll}", Path.GetFileName(dllPath));
                loadContext.Unload();
            }
        }
    }

    /// <summary>
    /// 在程序集中查找所有实现 IProtocolDriver 并标记了 DriverMetadata 的类型
    /// </summary>
    private static List<(Type DriverType, DriverMetadataAttribute Metadata)> FindDriverTypes(Assembly assembly)
    {
        var results = new List<(Type, DriverMetadataAttribute)>();

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract)
                continue;

            if (!typeof(IProtocolDriver).IsAssignableFrom(type))
                continue;

            var metadata = type.GetCustomAttribute<DriverMetadataAttribute>();
            if (metadata == null)
                continue;

            results.Add((type, metadata));
        }

        return results;
    }

    /// <summary>
    /// 在程序集中查找所有实现 IPublisher 并标记了 PublisherMetadata 的类型
    /// </summary>
    private static List<(Type PublisherType, PublisherMetadataAttribute Metadata)> FindPublisherTypes(Assembly assembly)
    {
        var results = new List<(Type, PublisherMetadataAttribute)>();

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract)
                continue;

            if (!typeof(IPublisher).IsAssignableFrom(type))
                continue;

            var metadata = type.GetCustomAttribute<PublisherMetadataAttribute>();
            if (metadata == null)
                continue;

            results.Add((type, metadata));
        }

        return results;
    }
}
