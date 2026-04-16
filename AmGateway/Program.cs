using AmGateway.Pipeline;
using AmGateway.PluginHost;
using AmGateway.Services;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/amgateway-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("AmGateway 启动中...");

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "AmGateway";
    });

    // 注册核心服务
    builder.Services.AddSingleton<GatewayMetrics>();
    builder.Services.AddSingleton<PluginManager>();
    builder.Services.AddSingleton<IDataPipeline, ChannelDataPipeline>();
    builder.Services.AddSingleton<ITransformEngine, TransformEngine>();
    builder.Services.AddSingleton<IRouteResolver, RouteResolver>();
    builder.Services.AddSingleton<IConfigRepository>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<SqliteConfigRepository>>();
        var dbPath = builder.Configuration.GetValue<string>("Gateway:DatabasePath");
        return new SqliteConfigRepository(logger, dbPath);
    });
    builder.Services.AddSingleton<GatewayRuntime>();
    builder.Services.AddHostedService<GatewayHostService>();

    // JWT 认证
    builder.AddGatewayJwtAuth();

    // Serilog 集成
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog();

    var app = builder.Build();

    // 设置全局 Runtime 访问器
    GatewayRuntimeAccessor.Runtime = app.Services.GetRequiredService<GatewayRuntime>();

    // 认证中间件 + 登录端点
    app.UseGatewayAuth();

    // 业务 API 端点
    app.MapGatewayApi();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AmGateway 启动失败");
}
finally
{
    Log.CloseAndFlush();
}
