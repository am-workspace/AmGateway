using System.Net.Sockets;
using AmGateway.Abstractions;
using AmGateway.Abstractions.Metadata;
using AmGateway.Abstractions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NModbus;

namespace AmGateway.Driver.Modbus;

/// <summary>
/// Modbus TCP 协议驱动 - 轮询读取 Modbus Server 寄存器数据
/// 核心通信逻辑复用自旧项目 PlcGateway/Services/ModbusClientService.cs
/// </summary>
[DriverMetadata(
    Name = "Modbus TCP",
    ProtocolName = "modbus-tcp",
    Version = "1.0.0",
    Description = "Modbus TCP 协议驱动，支持 HoldingRegister / InputRegister / Coil / DiscreteInput 读取")]
public sealed class ModbusTcpDriver : IProtocolDriver
{
    private ILogger<ModbusTcpDriver> _logger = null!;
    private IDataSink _dataSink = null!;
    private ModbusDriverConfig _config = null!;
    private TcpClient? _tcpClient;
    private IModbusMaster? _modbusMaster;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    public string DriverId { get; private set; } = string.Empty;

    public Task InitializeAsync(DriverContext context, CancellationToken ct = default)
    {
        DriverId = context.DriverInstanceId;
        _logger = context.LoggerFactory.CreateLogger<ModbusTcpDriver>();
        _dataSink = context.DataSink;

        _config = new ModbusDriverConfig();
        context.Configuration.Bind(_config);

        _logger.LogInformation("[{DriverId}] Modbus TCP 驱动已初始化 - 目标: {Host}:{Port}, 从站ID: {SlaveId}, 轮询间隔: {Interval}ms, 寄存器数: {Count}",
            DriverId, _config.Host, _config.Port, _config.SlaveId, _config.PollIntervalMs, _config.Registers.Count);

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollingTask = RunPollingAsync(_cts.Token);
        _logger.LogInformation("[{DriverId}] Modbus TCP 轮询已启动", DriverId);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
        }

        if (_pollingTask != null)
        {
            try
            {
                await _pollingTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("[{DriverId}] Modbus 停止超时", DriverId);
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
        }

        Disconnect();
        _logger.LogInformation("[{DriverId}] Modbus TCP 驱动已停止", DriverId);
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 轮询循环（复用旧项目 RunPollingAsync 逻辑）
    /// </summary>
    private async Task RunPollingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAsync(ct);
                var readOk = await ReadRegistersAsync(ct);

                if (!readOk)
                {
                    // 读取失败，断开连接并等待重连
                    Disconnect();

                    foreach (var reg in _config.Registers)
                    {
                        await _dataSink.PublishAsync(new DataPoint
                        {
                            Tag = $"{DriverId}/{reg.Name}",
                            Value = null,
                            Timestamp = DateTimeOffset.UtcNow,
                            Quality = DataQuality.NotConnected,
                            SourceDriver = DriverId
                        }, ct);
                    }

                    try { await Task.Delay(_config.ReconnectDelayMs, ct); }
                    catch (OperationCanceledException) { break; }

                    continue; // 跳过 PollInterval 等待，立即重试连接
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{DriverId}] Modbus 连接异常，等待重连", DriverId);
                Disconnect();

                foreach (var reg in _config.Registers)
                {
                    await _dataSink.PublishAsync(new DataPoint
                    {
                        Tag = $"{DriverId}/{reg.Name}",
                        Value = null,
                        Timestamp = DateTimeOffset.UtcNow,
                        Quality = DataQuality.NotConnected,
                        SourceDriver = DriverId
                    }, ct);
                }

                try { await Task.Delay(_config.ReconnectDelayMs, ct); }
                catch (OperationCanceledException) { break; }

                continue;
            }

            try
            {
                await Task.Delay(_config.PollIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 确保 Modbus TCP 连接（复用旧项目 EnsureConnectedAsync 逻辑）
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_modbusMaster != null) return;

        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(_config.Host, _config.Port, ct);

        var factory = new ModbusFactory();
        _modbusMaster = factory.CreateMaster(_tcpClient);

        _logger.LogInformation("[{DriverId}] Modbus 已连接到 {Host}:{Port}", DriverId, _config.Host, _config.Port);
    }

    /// <summary>
    /// 读取所有寄存器并输出 DataPoint。返回 true 表示全部成功，false 表示有读取失败需重连
    /// </summary>
    private async Task<bool> ReadRegistersAsync(CancellationToken ct)
    {
        if (_modbusMaster == null) return false;

        var allOk = true;
        foreach (var register in _config.Registers)
        {
            try
            {
                var rawValue = await ReadRegisterValueAsync(register);
                object value;
                if (register.Type is "Coil" or "DiscreteInput")
                {
                    value = rawValue;
                }
                else
                {
                    var scaled = Math.Round(Convert.ToDouble(rawValue) * register.Scale + register.Offset, register.Precision);
                    value = (decimal)scaled;
                }

                await _dataSink.PublishAsync(new DataPoint
                {
                    Tag = $"{DriverId}/{register.Name}",
                    Value = value,
                    Timestamp = DateTimeOffset.UtcNow,
                    Quality = DataQuality.Good,
                    SourceDriver = DriverId,
                    Metadata = new Dictionary<string, string>
                    {
                        ["address"] = register.Address.ToString(),
                        ["type"] = register.Type,
                        ["protocol"] = "modbus-tcp",
                        ["scale"] = register.Scale.ToString(),
                        ["offset"] = register.Offset.ToString()
                    }
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{DriverId}] 读取寄存器 {Name} (地址:{Address}) 失败，将触发重连",
                    DriverId, register.Name, register.Address);

                await _dataSink.PublishAsync(new DataPoint
                {
                    Tag = $"{DriverId}/{register.Name}",
                    Value = null,
                    Timestamp = DateTimeOffset.UtcNow,
                    Quality = DataQuality.Bad,
                    SourceDriver = DriverId
                }, ct);

                allOk = false;
                break; // 一个寄存器失败就够了，跳出 foreach
            }
        }

        return allOk;
    }

    /// <summary>
    /// 根据寄存器类型读取值（复用旧项目 switch 分发逻辑，扩展支持 Coil / DiscreteInput）
    /// </summary>
    private async Task<object> ReadRegisterValueAsync(RegisterDefinition register)
    {
        var slaveId = _config.SlaveId;
        var address = (ushort)register.Address;
        var count = (ushort)register.Count;

        return register.Type switch
        {
            "HoldingRegister" => (await _modbusMaster!.ReadHoldingRegistersAsync(slaveId, address, count))[0],
            "InputRegister" => (await _modbusMaster!.ReadInputRegistersAsync(slaveId, address, count))[0],
            "Coil" => (await _modbusMaster!.ReadCoilsAsync(slaveId, address, count))[0],
            "DiscreteInput" => (await _modbusMaster!.ReadInputsAsync(slaveId, address, count))[0],
            _ => throw new NotSupportedException($"不支持的寄存器类型: {register.Type}")
        };
    }

    /// <summary>
    /// 断开 Modbus TCP 连接
    /// </summary>
    private void Disconnect()
    {
        _modbusMaster?.Dispose();
        _modbusMaster = null;
        _tcpClient?.Close();
        _tcpClient?.Dispose();
        _tcpClient = null;
    }
}

/// <summary>
/// Modbus 驱动内部配置
/// </summary>
internal sealed class ModbusDriverConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    public byte SlaveId { get; set; } = 1;
    public int PollIntervalMs { get; set; } = 1000;
    public int ReconnectDelayMs { get; set; } = 5000;
    public List<RegisterDefinition> Registers { get; set; } = [];
}

/// <summary>
/// Modbus 寄存器定义
/// </summary>
internal sealed class RegisterDefinition
{
    public int Address { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "HoldingRegister";
    public int Count { get; set; } = 1;
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public int Precision { get; set; } = 2;
}
