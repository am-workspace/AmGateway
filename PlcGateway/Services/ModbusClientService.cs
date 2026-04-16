using NModbus;
using PlcGateway.Configuration;
using System.Net.Sockets;

namespace PlcGateway.Services;

/// <summary>
/// Modbus 客户端服务 - 轮询读取 Modbus Server 数据
/// </summary>
public class ModbusClientService : IDisposable
{
    private readonly ModbusSettings _settings;
    private readonly MqttPublishService _mqttPublish;
    private readonly ILogger<ModbusClientService> _logger;
    private TcpClient? _tcpClient;
    private IModbusMaster? _modbusMaster;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    public ModbusClientService(
        ModbusSettings settings,
        MqttPublishService mqttPublish,
        ILogger<ModbusClientService> logger)
    {
        _settings = settings;
        _mqttPublish = mqttPublish;
        _logger = logger;
    }

    /// <summary>
    /// 启动 Modbus 客户端
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("[Modbus] 服务已禁用");
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingTask = RunPollingAsync(_cts.Token);
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// 停止 Modbus 客户端
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
        }

        if (_pollingTask != null)
        {
            try
            {
                await _pollingTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("[Modbus] 停止超时");
            }
        }

        Disconnect();
    }

    /// <summary>
    /// 轮询循环
    /// </summary>
    private async Task RunPollingAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAsync(cancellationToken);
                await ReadAndPublishAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Modbus] 轮询异常");
                Disconnect();
                await Task.Delay(5000, cancellationToken); // 重连间隔
            }

            await Task.Delay(_settings.PollIntervalMs, cancellationToken);
        }
    }

    /// <summary>
    /// 确保连接
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_modbusMaster != null) return;

        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(_settings.Host, _settings.Port, cancellationToken);

        var factory = new ModbusFactory();
        _modbusMaster = factory.CreateMaster(_tcpClient);

        _logger.LogInformation("[Modbus] 已连接到 {Host}:{Port}", _settings.Host, _settings.Port);
    }

    /// <summary>
    /// 读取寄存器并发布到 MQTT
    /// </summary>
    private async Task ReadAndPublishAsync(CancellationToken cancellationToken)
    {
        if (_modbusMaster == null) return;

        foreach (var register in _settings.Registers)
        {
            try
            {
                ushort[] values = register.Type switch
                {
                    "HoldingRegister" => await _modbusMaster.ReadHoldingRegistersAsync(1, (ushort)register.Address, 1),
                    "InputRegister" => await _modbusMaster.ReadInputRegistersAsync(1, (ushort)register.Address, 1),
                    _ => throw new NotSupportedException($"不支持的寄存器类型: {register.Type}")
                };

                if (values.Length > 0)
                {
                    _logger.LogInformation("[Modbus] 读取 {Name} = {Value}", register.Name, values[0]);
                    await _mqttPublish.PublishAsync("modbus", register.Name.ToLowerInvariant(), values[0], cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Modbus] 读取寄存器 {Name} 失败", register.Name);
            }
        }
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    private void Disconnect()
    {
        _modbusMaster?.Dispose();
        _modbusMaster = null;
        _tcpClient?.Close();
        _tcpClient?.Dispose();
        _tcpClient = null;
        _logger.LogInformation("[Modbus] 已断开连接");
    }

    public void Dispose()
    {
        Disconnect();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
