using PlcGateway;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/plcgateway-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("PlcGateway 启动中...");

    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "PlcGateway";
    });

    builder.Services.AddHostedService<Worker>();

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "PlcGateway 启动失败");
}
finally
{
    Log.CloseAndFlush();
}
