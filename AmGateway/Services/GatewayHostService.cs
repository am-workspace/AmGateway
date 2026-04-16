using AmGateway.Pipeline;
using AmGateway.PluginHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AmGateway.Services;

/// <summary>
/// 网关主机服务 - 启动入口，将核心管理委托给 GatewayRuntime
/// 启动顺序: 管道 → 发布器 → 驱动
/// 停止顺序: 驱动 → 管道排空 → 发布器
/// </summary>
public sealed class GatewayHostService : BackgroundService
{
    private readonly PluginManager _pluginManager;
    private readonly IDataPipeline _pipeline;
    private readonly ITransformEngine _transformEngine;
    private readonly IRouteResolver _routeResolver;
    private readonly GatewayMetrics _metrics;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GatewayHostService> _logger;

    public GatewayHostService(
        PluginManager pluginManager,
        IDataPipeline pipeline,
        ITransformEngine transformEngine,
        IRouteResolver routeResolver,
        GatewayMetrics metrics,
        IConfiguration configuration,
        ILogger<GatewayHostService> logger)
    {
        _pluginManager = pluginManager;
        _pipeline = pipeline;
        _transformEngine = transformEngine;
        _routeResolver = routeResolver;
        _metrics = metrics;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[GatewayHost] 网关服务启动中...");

        try
        {
            // 1. 发现插件
            var pluginsPath = _configuration.GetValue<string>("Gateway:PluginsPath") ?? "plugins";
            _pluginManager.DiscoverPlugins(pluginsPath);

            // 2. 获取 GatewayRuntime
            var runtime = GatewayRuntimeAccessor.Runtime!;

            // 3. 首次启动种子 appsettings.json 到持久化
            await runtime.SeedFromAppSettingsAsync(stoppingToken);

            // 4. 从持久化加载并启动发布器
            await runtime.StartPublishersFromPersistenceAsync(stoppingToken);
            _pipeline.SetPublishers(runtime.GetPublisherInstances());

            // 5. 设置转换引擎和路由解析器
            _pipeline.SetTransformEngine(_transformEngine);
            _pipeline.SetRouteResolver(_routeResolver);

            // 6. 从持久化加载转换和路由规则
            await runtime.LoadTransformRulesFromPersistenceAsync(stoppingToken);
            await runtime.LoadRouteRulesFromPersistenceAsync(stoppingToken);

            // 7. 启动数据管道消费者
            await _pipeline.StartConsumingAsync(stoppingToken);

            // 8. 从持久化加载并启动驱动
            await runtime.StartDriversFromPersistenceAsync(stoppingToken);

            _logger.LogInformation("[GatewayHost] 网关服务已启动，活跃驱动: {DriverCount}, 活跃发布器: {PublisherCount}",
                runtime.ActiveDriverCount, runtime.ActivePublisherCount);

            // 9. 指标更新循环（每5秒更新一次仪表盘指标）
            using var metricsTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await metricsTimer.WaitForNextTickAsync(stoppingToken))
            {
                _metrics.SetActiveDrivers(runtime.ActiveDriverCount);
                _metrics.SetActivePublishers(runtime.ActivePublisherCount);
                _metrics.SetChannelCount(_pipeline.PendingCount);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[GatewayHost] 收到取消信号");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GatewayHost] 启动失败");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[GatewayHost] 正在优雅停止网关服务...");

        var runtime = GatewayRuntimeAccessor.Runtime;
        if (runtime == null)
        {
            _logger.LogWarning("[GatewayHost] Runtime 未初始化，直接退出");
            return;
        }

        var drainTimeout = _configuration.GetValue<int>("Gateway:DrainTimeoutSeconds", 10);

        // 阶段1: 停止所有驱动（不再产生新数据）
        _logger.LogInformation("[GatewayHost] 阶段1: 停止所有驱动...");
        foreach (var kvp in runtime.GetDriverInfos().Select(d => d.InstanceId).ToList())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await runtime.RemoveDriverAsync(kvp, cts.Token, removePersistence: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GatewayHost] 停止驱动 {InstanceId} 时出错", kvp);
            }
        }
        _logger.LogInformation("[GatewayHost] 阶段1: 所有驱动已停止");

        // 阶段2: 排空管道中的残余数据
        _logger.LogInformation("[GatewayHost] 阶段2: 排空管道数据（超时 {Timeout}s）...", drainTimeout);
        var drainSw = System.Diagnostics.Stopwatch.StartNew();
        var drainDeadline = TimeSpan.FromSeconds(drainTimeout);

        while (_pipeline.PendingCount > 0 && drainSw.Elapsed < drainDeadline)
        {
            _logger.LogDebug("[GatewayHost] 管道中仍有 {Count} 个数据点待处理...", _pipeline.PendingCount);
            await Task.Delay(200, cancellationToken);
        }

        if (_pipeline.PendingCount > 0)
        {
            _logger.LogWarning("[GatewayHost] 管道排空超时，仍有 {Count} 个数据点未处理", _pipeline.PendingCount);
        }
        else
        {
            _logger.LogInformation("[GatewayHost] 阶段2: 管道已排空");
        }

        // 阶段3: 停止消费循环
        await _pipeline.StopAsync();
        _logger.LogInformation("[GatewayHost] 阶段3: 管道消费者已停止");

        // 阶段4: 停止所有发布器
        _logger.LogInformation("[GatewayHost] 阶段4: 停止所有发布器...");
        foreach (var kvp in runtime.GetPublisherInfos().Select(p => p.InstanceId).ToList())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await runtime.RemovePublisherAsync(kvp, cts.Token, removePersistence: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GatewayHost] 停止发布器 {InstanceId} 时出错", kvp);
            }
        }
        _logger.LogInformation("[GatewayHost] 阶段4: 所有发布器已停止");

        // 阶段5: 卸载插件
        _pluginManager.UnloadAll();

        _logger.LogInformation("[GatewayHost] 网关服务已优雅停止");
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// GatewayRuntime 全局访问器
/// </summary>
public static class GatewayRuntimeAccessor
{
    public static GatewayRuntime? Runtime { get; set; }
}
