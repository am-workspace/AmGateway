using AmGateway.Abstractions;
using AmGateway.Abstractions.Metadata;
using AmGateway.Abstractions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using S7.Net;

namespace AmGateway.Driver.S7;

/// <summary>
/// Siemens S7 协议驱动 - 轮询读取 S7 PLC 数据块
/// </summary>
[DriverMetadata(
    Name = "Siemens S7",
    ProtocolName = "s7",
    Version = "1.0.0",
    Description = "Siemens S7 协议驱动，支持 S7-200/300/400/1200/1500 系列 PLC")]
public sealed class S7Driver : IProtocolDriver
{
    private ILogger<S7Driver> _logger = null!;
    private IDataSink _dataSink = null!;
    private S7DriverConfig _config = null!;
    private Plc? _plc;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    public string DriverId { get; private set; } = string.Empty;

    public Task InitializeAsync(DriverContext context, CancellationToken ct = default)
    {
        DriverId = context.DriverInstanceId;
        _logger = context.LoggerFactory.CreateLogger<S7Driver>();
        _dataSink = context.DataSink;

        _config = new S7DriverConfig();
        context.Configuration.Bind(_config);

        _logger.LogInformation("[{DriverId}] S7 驱动已初始化 - PLC: {Ip}, CPU: {CpuType}, Rack: {Rack}, Slot: {Slot}, 数据项: {Count}",
            DriverId, _config.IpAddress, _config.CpuType, _config.Rack, _config.Slot, _config.DataItems.Count);

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollingTask = RunPollingAsync(_cts.Token);
        _logger.LogInformation("[{DriverId}] S7 轮询已启动", DriverId);
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
                _logger.LogWarning("[{DriverId}] S7 停止超时", DriverId);
            }
            catch (OperationCanceledException) { }
        }

        Disconnect();
        _logger.LogInformation("[{DriverId}] S7 驱动已停止", DriverId);
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task RunPollingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAsync(ct);
                await ReadDataItemsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{DriverId}] S7 轮询异常", DriverId);
                Disconnect();

                foreach (var item in _config.DataItems)
                {
                    await _dataSink.PublishAsync(new DataPoint
                    {
                        Tag = $"{DriverId}/{item.Name}",
                        Value = null,
                        Timestamp = DateTimeOffset.UtcNow,
                        Quality = DataQuality.NotConnected,
                        SourceDriver = DriverId
                    }, ct);
                }

                try { await Task.Delay(_config.ReconnectDelayMs, ct); }
                catch (OperationCanceledException) { break; }
            }

            try { await Task.Delay(_config.PollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_plc is { IsConnected: true }) return;

        var cpuType = Enum.Parse<CpuType>(_config.CpuType, ignoreCase: true);
        _plc = new Plc(cpuType, _config.IpAddress, _config.Rack, _config.Slot);
        await _plc.OpenAsync(ct);

        _logger.LogInformation("[{DriverId}] S7 已连接到 {Ip} ({CpuType})", DriverId, _config.IpAddress, _config.CpuType);
    }

    private async Task ReadDataItemsAsync(CancellationToken ct)
    {
        if (_plc == null || !_plc.IsConnected) return;

        foreach (var item in _config.DataItems)
        {
            try
            {
                var value = await ReadSingleItemAsync(item);

                await _dataSink.PublishAsync(new DataPoint
                {
                    Tag = $"{DriverId}/{item.Name}",
                    Value = value,
                    Timestamp = DateTimeOffset.UtcNow,
                    Quality = DataQuality.Good,
                    SourceDriver = DriverId,
                    Metadata = new Dictionary<string, string>
                    {
                        ["dbNumber"] = item.DbNumber.ToString(),
                        ["startByte"] = item.StartByte.ToString(),
                        ["dataType"] = item.DataType,
                        ["protocol"] = "s7"
                    }
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{DriverId}] 读取 S7 数据项 {Name} 失败", DriverId, item.Name);

                await _dataSink.PublishAsync(new DataPoint
                {
                    Tag = $"{DriverId}/{item.Name}",
                    Value = null,
                    Timestamp = DateTimeOffset.UtcNow,
                    Quality = DataQuality.Bad,
                    SourceDriver = DriverId
                }, ct);
            }
        }
    }

    private async Task<object?> ReadSingleItemAsync(S7DataItemDefinition item)
    {
        var dataType = ParseDataType(item.DataType);
        var varType = ParseVarType(item.DataType);
        var result = await _plc!.ReadAsync(dataType, item.DbNumber, item.StartByte, varType, item.Count);
        return result;
    }

    private static DataType ParseDataType(string dataType)
    {
        return dataType.ToUpperInvariant() switch
        {
            "COUNTER" => DataType.Counter,
            "TIMER" => DataType.Timer,
            _ => DataType.DataBlock
        };
    }

    private static VarType ParseVarType(string dataType)
    {
        return dataType.ToUpperInvariant() switch
        {
            "BOOL" or "BIT" => VarType.Bit,
            "BYTE" => VarType.Byte,
            "INT" => VarType.Int,
            "DINT" => VarType.DInt,
            "REAL" => VarType.Real,
            "DWORD" => VarType.DWord,
            "WORD" => VarType.Word,
            "STRING" => VarType.String,
            "COUNTER" => VarType.Counter,
            "TIMER" => VarType.Timer,
            _ => VarType.Byte
        };
    }

    private void Disconnect()
    {
        if (_plc != null)
        {
            _plc.Close();
            _plc = null;
        }
    }
}

internal sealed class S7DriverConfig
{
    public string IpAddress { get; set; } = "192.168.1.30";
    public string CpuType { get; set; } = "S71200";
    public short Rack { get; set; } = 0;
    public short Slot { get; set; } = 0;
    public int PollIntervalMs { get; set; } = 1000;
    public int ReconnectDelayMs { get; set; } = 5000;
    public List<S7DataItemDefinition> DataItems { get; set; } = [];
}

internal sealed class S7DataItemDefinition
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = "Real";
    public int DbNumber { get; set; }
    public int StartByte { get; set; }
    public int BitIndex { get; set; } = 0;
    public int Count { get; set; } = 1;
}
