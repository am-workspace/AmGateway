using PlcGateway.Configuration;
using PlcGateway.Services;

namespace PlcGateway;

/// <summary>
/// 后台工作器 - 协调启动和停止各协议服务
/// </summary>
public class Worker : BackgroundService
{
    private readonly MqttSettings _mqttSettings;
    private readonly ModbusSettings _modbusSettings;
    private readonly OpcUaSettings _opcUaSettings;
    private readonly ILogger<Worker> _logger;
    private MqttPublishService? _mqttPublish;
    private ModbusClientService? _modbusClient;
    private OpcUaClientService? _opcUaClient;

    public Worker(
        IConfiguration configuration,
        ILogger<Worker> logger)
    {
        _mqttSettings = configuration.GetSection("Mqtt").Get<MqttSettings>() ?? new MqttSettings();
        _modbusSettings = configuration.GetSection("Modbus").Get<ModbusSettings>() ?? new ModbusSettings();
        _opcUaSettings = configuration.GetSection("OpcUa").Get<OpcUaSettings>() ?? new OpcUaSettings();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Worker] PlcGateway 启动中...");

        try
        {
            // 1. 先连接 MQTT（其他服务依赖它）
            _mqttPublish = new MqttPublishService(_mqttSettings, 
                LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<MqttPublishService>());
            await _mqttPublish.ConnectAsync(stoppingToken);

            // 2. 启动 Modbus 客户端
            if (_modbusSettings.Enabled)
            {
                _modbusClient = new ModbusClientService(_modbusSettings, _mqttPublish,
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<ModbusClientService>());
                await _modbusClient.StartAsync(stoppingToken);
            }

            // 3. 启动 OPC UA 客户端
            if (_opcUaSettings.Enabled)
            {
                _opcUaClient = new OpcUaClientService(_opcUaSettings, _mqttPublish,
                    LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<OpcUaClientService>());
                await _opcUaClient.StartAsync(stoppingToken);
            }

            _logger.LogInformation("[Worker] 所有服务已启动");

            // 保持运行直到取消
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Worker] 收到取消信号");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Worker] 启动失败");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Worker] 正在停止服务...");

        if (_opcUaClient != null)
        {
            await _opcUaClient.StopAsync(cancellationToken);
            _opcUaClient.Dispose();
        }

        if (_modbusClient != null)
        {
            await _modbusClient.StopAsync(cancellationToken);
            _modbusClient.Dispose();
        }

        _mqttPublish?.Dispose();

        _logger.LogInformation("[Worker] 所有服务已停止");
        await base.StopAsync(cancellationToken);
    }
}
