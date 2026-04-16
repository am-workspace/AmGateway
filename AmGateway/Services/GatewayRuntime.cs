using System.Collections.Concurrent;
using System.Text.Json;
using AmGateway.Abstractions;
using AmGateway.Abstractions.Models;
using AmGateway.Pipeline;
using AmGateway.PluginHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AmGateway.Services;

/// <summary>
/// 网关运行时 — 管理驱动和发布器实例的生命周期
/// 启动时从持久化加载配置，运行时变更自动持久化
/// </summary>
public sealed class GatewayRuntime
{
    private readonly PluginManager _pluginManager;
    private readonly IDataPipeline _pipeline;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GatewayRuntime> _logger;
    private readonly IConfigRepository _configRepo;
    private readonly ITransformEngine _transformEngine;
    private readonly IRouteResolver _routeResolver;

    private readonly ConcurrentDictionary<string, DriverEntry> _drivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PublisherEntry> _publishers = new(StringComparer.OrdinalIgnoreCase);

    public GatewayRuntime(
        PluginManager pluginManager,
        IDataPipeline pipeline,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IConfigRepository configRepo,
        ITransformEngine transformEngine,
        IRouteResolver routeResolver,
        ILogger<GatewayRuntime> logger)
    {
        _pluginManager = pluginManager;
        _pipeline = pipeline;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _configRepo = configRepo;
        _transformEngine = transformEngine;
        _routeResolver = routeResolver;
        _logger = logger;
    }

    /// <summary>当前活跃驱动数</summary>
    public int ActiveDriverCount => _drivers.Count;

    /// <summary>当前活跃发布器数</summary>
    public int ActivePublisherCount => _publishers.Count;

    /// <summary>
    /// 首次启动时将 appsettings.json 中的配置种子到持久化存储
/// 后续启动统一从持久化加载
    /// </summary>
    public async Task SeedFromAppSettingsAsync(CancellationToken ct)
    {
        var existingDrivers = await _configRepo.GetAllDriversAsync();
        var existingPublishers = await _configRepo.GetAllPublishersAsync();

        // 如果持久化已有数据，不覆盖
        if (existingDrivers.Count > 0 || existingPublishers.Count > 0)
        {
            _logger.LogInformation("[Runtime] 持久化存储已有 {DriverCount} 个驱动, {PublisherCount} 个发布器，跳过种子",
                existingDrivers.Count, existingPublishers.Count);
            return;
        }

        _logger.LogInformation("[Runtime] 持久化存储为空，从 appsettings.json 种子初始化...");

        // 种子发布器
        var publishersSection = _configuration.GetSection("Publishers");
        foreach (var pubConfig in publishersSection.GetChildren())
        {
            var transport = pubConfig.GetValue<string>("Transport");
            var instanceId = pubConfig.GetValue<string>("InstanceId") ?? $"{transport}-{Guid.NewGuid():N[..8]}";
            var enabled = pubConfig.GetValue<bool>("Enabled", true);

            if (string.IsNullOrEmpty(transport)) continue;

            var settingsJson = ConfigurationToJson(pubConfig.GetSection("Settings"));
            await _configRepo.SavePublisherAsync(new PublisherConfigRecord
            {
                InstanceId = instanceId,
                Transport = transport,
                Enabled = enabled,
                SettingsJson = settingsJson
            });
        }

        // 种子驱动
        var driversSection = _configuration.GetSection("Drivers");
        foreach (var drvConfig in driversSection.GetChildren())
        {
            var protocol = drvConfig.GetValue<string>("Protocol");
            var instanceId = drvConfig.GetValue<string>("InstanceId") ?? $"{protocol}-{Guid.NewGuid():N[..8]}";
            var enabled = drvConfig.GetValue<bool>("Enabled", true);

            if (string.IsNullOrEmpty(protocol)) continue;

            var settingsJson = ConfigurationToJson(drvConfig.GetSection("Settings"));
            await _configRepo.SaveDriverAsync(new DriverConfigRecord
            {
                InstanceId = instanceId,
                Protocol = protocol,
                Enabled = enabled,
                SettingsJson = settingsJson
            });
        }

        _logger.LogInformation("[Runtime] 种子初始化完成");
    }

    /// <summary>
    /// 从持久化存储加载并启动所有发布器
    /// </summary>
    public async Task StartPublishersFromPersistenceAsync(CancellationToken ct)
    {
        var records = await _configRepo.GetAllPublishersAsync();
        _logger.LogInformation("[Runtime] 持久化存储中 {Count} 个发布器配置", records.Count);

        foreach (var record in records)
        {
            if (!record.Enabled)
            {
                _logger.LogInformation("[Runtime] 发布器 {InstanceId} ({Transport}) 已禁用，跳过",
                    record.InstanceId, record.Transport);
                continue;
            }

            try
            {
                var config = BuildConfigurationFromJson(record.SettingsJson);
                await AddPublisherAsync(record.Transport, record.InstanceId, config, ct, persist: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Runtime] 启动发布器 {InstanceId} ({Transport}) 失败",
                    record.InstanceId, record.Transport);
            }
        }
    }

    /// <summary>
    /// 从持久化存储加载并启动所有驱动
    /// </summary>
    public async Task StartDriversFromPersistenceAsync(CancellationToken ct)
    {
        var records = await _configRepo.GetAllDriversAsync();
        _logger.LogInformation("[Runtime] 持久化存储中 {Count} 个驱动配置", records.Count);

        foreach (var record in records)
        {
            if (!record.Enabled)
            {
                _logger.LogInformation("[Runtime] 驱动 {InstanceId} ({Protocol}) 已禁用，跳过",
                    record.InstanceId, record.Protocol);
                continue;
            }

            try
            {
                var config = BuildConfigurationFromJson(record.SettingsJson);
                await AddDriverAsync(record.Protocol, record.InstanceId, config, ct, persist: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Runtime] 启动驱动 {InstanceId} ({Protocol}) 失败",
                    record.InstanceId, record.Protocol);
            }
        }
    }

    /// <summary>
    /// 动态添加并启动一个驱动实例
    /// </summary>
    public async Task<DriverInstanceInfo> AddDriverAsync(
        string protocol, string instanceId, IConfiguration settings,
        CancellationToken ct, bool persist = true)
    {
        if (_drivers.ContainsKey(instanceId))
        {
            throw new InvalidOperationException($"驱动实例 {instanceId} 已存在");
        }

        var info = new DriverInstanceInfo
        {
            InstanceId = instanceId,
            Protocol = protocol,
            Status = RuntimeStatus.Starting
        };

        var entry = new DriverEntry { Info = info };

        if (!_drivers.TryAdd(instanceId, entry))
        {
            throw new InvalidOperationException($"驱动实例 {instanceId} 已存在");
        }

        try
        {
            var driver = _pluginManager.CreateDriverInstance(protocol);
            if (driver == null)
            {
                info.Status = RuntimeStatus.Error;
                info.ErrorMessage = $"无法创建协议 '{protocol}' 的驱动实例";
                _drivers.TryRemove(instanceId, out _);
                throw new InvalidOperationException(info.ErrorMessage);
            }

            var context = new DriverContext
            {
                Configuration = settings,
                LoggerFactory = _loggerFactory,
                DataSink = _pipeline.Writer,
                DriverInstanceId = instanceId
            };

            await driver.InitializeAsync(context, ct);
            await driver.StartAsync(ct);

            entry.Driver = driver;
            entry.Watchdog = new DriverWatchdog(instanceId, driver, this, _logger);
            info.Status = RuntimeStatus.Running;
            info.StartedAt = DateTimeOffset.UtcNow;

            // 持久化
            if (persist)
            {
                var settingsJson = ConfigurationToJson(settings);
                await _configRepo.SaveDriverAsync(new DriverConfigRecord
                {
                    InstanceId = instanceId,
                    Protocol = protocol,
                    Enabled = true,
                    SettingsJson = settingsJson
                });
            }

            _logger.LogInformation("[Runtime] 驱动 {InstanceId} ({Protocol}) 已启动", instanceId, protocol);
        }
        catch (Exception ex)
        {
            info.Status = RuntimeStatus.Error;
            info.ErrorMessage = ex.Message;
            _drivers.TryRemove(instanceId, out _);
            _logger.LogError(ex, "[Runtime] 启动驱动 {InstanceId} ({Protocol}) 失败", instanceId, protocol);
            throw;
        }

        return info;
    }

    /// <summary>
    /// 动态添加并启动一个发布器实例
    /// </summary>
    public async Task<PublisherInstanceInfo> AddPublisherAsync(
        string transport, string instanceId, IConfiguration settings,
        CancellationToken ct, bool persist = true)
    {
        if (_publishers.ContainsKey(instanceId))
        {
            throw new InvalidOperationException($"发布器实例 {instanceId} 已存在");
        }

        var info = new PublisherInstanceInfo
        {
            InstanceId = instanceId,
            Transport = transport,
            Status = RuntimeStatus.Starting
        };

        var entry = new PublisherEntry { Info = info };

        if (!_publishers.TryAdd(instanceId, entry))
        {
            throw new InvalidOperationException($"发布器实例 {instanceId} 已存在");
        }

        try
        {
            var publisher = _pluginManager.CreatePublisherInstance(transport);
            if (publisher == null)
            {
                info.Status = RuntimeStatus.Error;
                info.ErrorMessage = $"无法创建传输 '{transport}' 的发布器实例";
                _publishers.TryRemove(instanceId, out _);
                throw new InvalidOperationException(info.ErrorMessage);
            }

            var context = new PublisherContext
            {
                Configuration = settings,
                LoggerFactory = _loggerFactory,
                PublisherInstanceId = instanceId
            };

            await publisher.InitializeAsync(context, ct);
            await publisher.StartAsync(ct);

            entry.Publisher = publisher;
            info.Status = RuntimeStatus.Running;
            info.StartedAt = DateTimeOffset.UtcNow;

            // 动态注册到 Pipeline
            _pipeline.AddPublisher(publisher);

            // 持久化
            if (persist)
            {
                var settingsJson = ConfigurationToJson(settings);
                await _configRepo.SavePublisherAsync(new PublisherConfigRecord
                {
                    InstanceId = instanceId,
                    Transport = transport,
                    Enabled = true,
                    SettingsJson = settingsJson
                });
            }

            _logger.LogInformation("[Runtime] 发布器 {InstanceId} ({Transport}) 已启动", instanceId, transport);
        }
        catch (Exception ex)
        {
            info.Status = RuntimeStatus.Error;
            info.ErrorMessage = ex.Message;
            _publishers.TryRemove(instanceId, out _);
            _logger.LogError(ex, "[Runtime] 启动发布器 {InstanceId} ({Transport}) 失败", instanceId, transport);
            throw;
        }

        return info;
    }

    /// <summary>
    /// 停止并移除一个驱动实例
    /// </summary>
    public async Task RemoveDriverAsync(string instanceId, CancellationToken ct, bool removePersistence = true)
    {
        if (!_drivers.TryRemove(instanceId, out var entry))
        {
            throw new KeyNotFoundException($"驱动实例 {instanceId} 不存在");
        }

        entry.Info.Status = RuntimeStatus.Stopping;
        entry.Watchdog?.Dispose();

        try
        {
            if (entry.Driver != null)
            {
                await entry.Driver.StopAsync(ct);
                await entry.Driver.DisposeAsync();
            }

            entry.Info.Status = RuntimeStatus.Stopped;

            if (removePersistence)
            {
                await _configRepo.DeleteDriverAsync(instanceId);
            }

            _logger.LogInformation("[Runtime] 驱动 {InstanceId} 已停止并移除", instanceId);
        }
        catch (Exception ex)
        {
            entry.Info.Status = RuntimeStatus.Error;
            entry.Info.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Runtime] 停止驱动 {InstanceId} 时出错", instanceId);
            throw;
        }
    }

    /// <summary>
    /// 停止并移除一个发布器实例
    /// </summary>
    public async Task RemovePublisherAsync(string instanceId, CancellationToken ct, bool removePersistence = true)
    {
        if (!_publishers.TryRemove(instanceId, out var entry))
        {
            throw new KeyNotFoundException($"发布器实例 {instanceId} 不存在");
        }

        entry.Info.Status = RuntimeStatus.Stopping;

        try
        {
            if (entry.Publisher != null)
            {
                _pipeline.RemovePublisher(entry.Publisher);
                await entry.Publisher.StopAsync(ct);
                await entry.Publisher.DisposeAsync();
            }

            entry.Info.Status = RuntimeStatus.Stopped;

            if (removePersistence)
            {
                await _configRepo.DeletePublisherAsync(instanceId);
            }

            _logger.LogInformation("[Runtime] 发布器 {InstanceId} 已停止并移除", instanceId);
        }
        catch (Exception ex)
        {
            entry.Info.Status = RuntimeStatus.Error;
            entry.Info.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Runtime] 停止发布器 {InstanceId} 时出错", instanceId);
            throw;
        }
    }

    /// <summary>
    /// 重新启动一个处于 Error 状态的驱动
    /// </summary>
    public async Task RestartDriverAsync(string instanceId, CancellationToken ct)
    {
        if (!_drivers.TryGetValue(instanceId, out var entry))
        {
            throw new KeyNotFoundException($"驱动实例 {instanceId} 不存在");
        }

        // 先停止
        if (entry.Driver != null)
        {
            try { await entry.Driver.StopAsync(ct); } catch { /* 忽略 */ }
            try { await entry.Driver.DisposeAsync(); } catch { /* 忽略 */ }
        }

        entry.Watchdog?.Dispose();

        // 重新创建
        var record = await _configRepo.GetDriverAsync(instanceId);
        if (record == null)
        {
            throw new InvalidOperationException($"驱动 {instanceId} 的持久化配置不存在");
        }

        var config = BuildConfigurationFromJson(record.SettingsJson);
        var driver = _pluginManager.CreateDriverInstance(record.Protocol);
        if (driver == null)
        {
            entry.Info.Status = RuntimeStatus.Error;
            entry.Info.ErrorMessage = "无法创建驱动实例";
            return;
        }

        var context = new DriverContext
        {
            Configuration = config,
            LoggerFactory = _loggerFactory,
            DataSink = _pipeline.Writer,
            DriverInstanceId = instanceId
        };

        try
        {
            await driver.InitializeAsync(context, ct);
            await driver.StartAsync(ct);

            entry.Driver = driver;
            entry.Watchdog = new DriverWatchdog(instanceId, driver, this, _logger);
            entry.Info.Status = RuntimeStatus.Running;
            entry.Info.ErrorMessage = null;
            entry.Info.StartedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("[Runtime] 驱动 {InstanceId} 已重启", instanceId);
        }
        catch (Exception ex)
        {
            entry.Driver = null;
            entry.Info.Status = RuntimeStatus.Error;
            entry.Info.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Runtime] 重启驱动 {InstanceId} 失败", instanceId);
        }
    }

    /// <summary>获取所有驱动实例信息</summary>
    public IReadOnlyList<DriverInstanceInfo> GetDriverInfos() =>
        _drivers.Values.Select(e => e.Info).ToList();

    /// <summary>获取指定驱动实例信息</summary>
    public DriverInstanceInfo? GetDriverInfo(string instanceId) =>
        _drivers.TryGetValue(instanceId, out var entry) ? entry.Info : null;

    /// <summary>获取所有发布器实例信息</summary>
    public IReadOnlyList<PublisherInstanceInfo> GetPublisherInfos() =>
        _publishers.Values.Select(e => e.Info).ToList();

    /// <summary>获取指定发布器实例信息</summary>
    public PublisherInstanceInfo? GetPublisherInfo(string instanceId) =>
        _publishers.TryGetValue(instanceId, out var entry) ? entry.Info : null;

    /// <summary>获取所有运行中的发布器实例</summary>
    public IReadOnlyList<IPublisher> GetPublisherInstances() =>
        _publishers.Values.Where(e => e.Publisher != null).Select(e => e.Publisher!).ToList();

    /// <summary>获取已加载的插件信息</summary>
    public (IReadOnlyDictionary<string, PluginInfo> Drivers, IReadOnlyDictionary<string, PluginInfo> Publishers) GetLoadedPlugins() =>
        (_pluginManager.GetLoadedDriverPlugins(), _pluginManager.GetLoadedPublisherPlugins());

    /// <summary>获取配置仓库（用于 JSON 导出 API）</summary>
    public IConfigRepository ConfigRepo => _configRepo;

    // === 转换规则管理 ===

    /// <summary>
    /// 从持久化加载转换规则到引擎
    /// </summary>
    public async Task LoadTransformRulesFromPersistenceAsync(CancellationToken ct)
    {
        var records = await _configRepo.GetAllTransformRulesAsync();
        _logger.LogInformation("[Runtime] 持久化存储中 {Count} 条转换规则", records.Count);

        var rules = records.Select(r => new TransformRule
        {
            RuleId = r.RuleId,
            Enabled = r.Enabled,
            TagPattern = r.TagPattern,
            Type = r.Type,
            ParametersJson = r.ParametersJson,
            Priority = r.Priority
        }).ToList();

        _transformEngine.LoadRules(rules);
    }

    /// <summary>
    /// 添加转换规则（自动持久化 + 热更新引擎）
    /// </summary>
    public async Task<TransformRule> AddTransformRuleAsync(TransformRule rule, CancellationToken ct)
    {
        await _configRepo.SaveTransformRuleAsync(new TransformRuleRecord
        {
            RuleId = rule.RuleId,
            Enabled = rule.Enabled,
            TagPattern = rule.TagPattern,
            Type = rule.Type,
            ParametersJson = rule.ParametersJson,
            Priority = rule.Priority
        });

        // 热更新引擎
        var currentRules = _transformEngine.GetRules()
            .Where(r => !string.Equals(r.RuleId, rule.RuleId, StringComparison.OrdinalIgnoreCase))
            .Append(rule)
            .ToList();
        _transformEngine.LoadRules(currentRules);

        _logger.LogInformation("[Runtime] 转换规则 {RuleId} 已添加", rule.RuleId);
        return rule;
    }

    /// <summary>
    /// 删除转换规则
    /// </summary>
    public async Task RemoveTransformRuleAsync(string ruleId, CancellationToken ct)
    {
        await _configRepo.DeleteTransformRuleAsync(ruleId);

        // 热更新引擎
        var currentRules = _transformEngine.GetRules()
            .Where(r => !string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _transformEngine.LoadRules(currentRules);

        _logger.LogInformation("[Runtime] 转换规则 {RuleId} 已删除", ruleId);
    }

    /// <summary>获取所有转换规则</summary>
    public IReadOnlyList<TransformRule> GetTransformRules() => _transformEngine.GetRules();

    // === 路由规则管理 ===

    /// <summary>
    /// 从持久化加载路由规则到解析器
    /// </summary>
    public async Task LoadRouteRulesFromPersistenceAsync(CancellationToken ct)
    {
        var records = await _configRepo.GetAllRouteRulesAsync();
        _logger.LogInformation("[Runtime] 持久化存储中 {Count} 条路由规则", records.Count);

        var rules = records.Select(r => new RouteRule
        {
            RuleId = r.RuleId,
            Enabled = r.Enabled,
            TagPattern = r.TagPattern,
            TargetPublisherIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.TargetPublisherIdsJson) ?? [],
            Priority = r.Priority
        }).ToList();

        _routeResolver.LoadRules(rules);
    }

    /// <summary>
    /// 添加路由规则（自动持久化 + 热更新解析器）
    /// </summary>
    public async Task<RouteRule> AddRouteRuleAsync(RouteRule rule, CancellationToken ct)
    {
        await _configRepo.SaveRouteRuleAsync(new RouteRuleRecord
        {
            RuleId = rule.RuleId,
            Enabled = rule.Enabled,
            TagPattern = rule.TagPattern,
            TargetPublisherIdsJson = System.Text.Json.JsonSerializer.Serialize(rule.TargetPublisherIds),
            Priority = rule.Priority
        });

        // 热更新解析器
        var currentRules = _routeResolver.GetRules()
            .Where(r => !string.Equals(r.RuleId, rule.RuleId, StringComparison.OrdinalIgnoreCase))
            .Append(rule)
            .ToList();
        _routeResolver.LoadRules(currentRules);

        _logger.LogInformation("[Runtime] 路由规则 {RuleId} 已添加", rule.RuleId);
        return rule;
    }

    /// <summary>
    /// 删除路由规则
    /// </summary>
    public async Task RemoveRouteRuleAsync(string ruleId, CancellationToken ct)
    {
        await _configRepo.DeleteRouteRuleAsync(ruleId);

        // 热更新解析器
        var currentRules = _routeResolver.GetRules()
            .Where(r => !string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _routeResolver.LoadRules(currentRules);

        _logger.LogInformation("[Runtime] 路由规则 {RuleId} 已删除", ruleId);
    }

    /// <summary>获取所有路由规则</summary>
    public IReadOnlyList<RouteRule> GetRouteRules() => _routeResolver.GetRules();

    /// <summary>
    /// 停止所有实例（关机阶段调用）
    /// </summary>
    public async Task StopAllAsync(CancellationToken ct)
    {
        var shutdownTimeout = _configuration.GetValue<int>("Gateway:ShutdownTimeoutSeconds", 30);

        foreach (var kvp in _drivers.ToList())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(shutdownTimeout));
                await RemoveDriverAsync(kvp.Key, cts.Token, removePersistence: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Runtime] 停止驱动 {InstanceId} 时出错", kvp.Key);
            }
        }

        foreach (var kvp in _publishers.ToList())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(shutdownTimeout));
                await RemovePublisherAsync(kvp.Key, cts.Token, removePersistence: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Runtime] 停止发布器 {InstanceId} 时出错", kvp.Key);
            }
        }
    }

    /// <summary>从 JSON 字符串构建 IConfiguration</summary>
    private static IConfiguration BuildConfigurationFromJson(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict!.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)))
            .Build();
    }

    /// <summary>将 IConfiguration 序列化为 JSON</summary>
    private static string ConfigurationToJson(IConfiguration config)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var child in config.GetChildren())
        {
            FlattenConfig(dict, child.Key, child);
        }
        return JsonSerializer.Serialize(dict);
    }

    private static void FlattenConfig(Dictionary<string, object?> dict, string prefix, IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            dict[prefix] = section.Value;
        }
        else
        {
            foreach (var child in children)
            {
                FlattenConfig(dict, $"{prefix}:{child.Key}", child);
            }
        }
    }

    /// <summary>驱动实例内部条目</summary>
    internal sealed class DriverEntry
    {
        public DriverInstanceInfo Info { get; set; } = null!;
        public IProtocolDriver? Driver { get; set; }
        public DriverWatchdog? Watchdog { get; set; }
    }

    /// <summary>发布器实例内部条目</summary>
    private sealed class PublisherEntry
    {
        public PublisherInstanceInfo Info { get; set; } = null!;
        public IPublisher? Publisher { get; set; }
    }
}
