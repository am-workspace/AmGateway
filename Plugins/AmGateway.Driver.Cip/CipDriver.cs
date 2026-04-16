using AmGateway.Abstractions;
using AmGateway.Abstractions.Metadata;
using AmGateway.Abstractions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using libplctag;
using libplctag.DataTypes;

namespace AmGateway.Driver.Cip;

/// <summary>
/// CIP/EtherNet/IP 协议驱动 - 基于 libplctag 轮询读取 Allen-Bradley / Rockwell PLC 标签
/// </summary>
[DriverMetadata(
    Name = "CIP EtherNet/IP",
    ProtocolName = "cip",
    Version = "1.0.0",
    Description = "CIP/EtherNet/IP 协议驱动，支持 Allen-Bradley ControlLogix/CompactLogix 系列 PLC")]
public sealed class CipDriver : IProtocolDriver
{
    private ILogger<CipDriver> _logger = null!;
    private IDataSink _dataSink = null!;
    private CipDriverConfig _config = null!;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private readonly List<TagHandle> _tagHandles = [];

    public string DriverId { get; private set; } = string.Empty;

    public Task InitializeAsync(DriverContext context, CancellationToken ct = default)
    {
        DriverId = context.DriverInstanceId;
        _logger = context.LoggerFactory.CreateLogger<CipDriver>();
        _dataSink = context.DataSink;

        _config = new CipDriverConfig();
        context.Configuration.Bind(_config);

        // 设置 libplctag 全局调试级别
        LibPlcTag.DebugLevel = (DebugLevel)_config.LibDebugLevel;

        _logger.LogInformation(
            "[{DriverId}] CIP 驱动已初始化 - Gateway: {Gateway}, Path: {Path}, PlcType: {PlcType}, 标签数: {Count}",
            DriverId, _config.Gateway, _config.Path, _config.PlcType, _config.Tags.Count);

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollingTask = RunPollingAsync(_cts.Token);
        _logger.LogInformation("[{DriverId}] CIP 轮询已启动", DriverId);
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
                _logger.LogWarning("[{DriverId}] CIP 停止超时", DriverId);
            }
            catch (OperationCanceledException) { }
        }

        DestroyTags();
        _logger.LogInformation("[{DriverId}] CIP 驱动已停止", DriverId);
    }

    public ValueTask DisposeAsync()
    {
        DestroyTags();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task RunPollingAsync(CancellationToken ct)
    {
        EnsureTagsCreated();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReadAllTagsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{DriverId}] CIP 轮询异常", DriverId);

                // 上报所有标签断连状态
                foreach (var handle in _tagHandles)
                {
                    await _dataSink.PublishAsync(new DataPoint
                    {
                        Tag = $"{DriverId}/{handle.Definition.Name}",
                        Value = null,
                        Timestamp = DateTimeOffset.UtcNow,
                        Quality = DataQuality.NotConnected,
                        SourceDriver = DriverId
                    }, ct);
                }

                // 销毁并重建标签
                DestroyTags();

                try { await Task.Delay(_config.ReconnectDelayMs, ct); }
                catch (OperationCanceledException) { break; }

                EnsureTagsCreated();
            }

            try { await Task.Delay(_config.PollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void EnsureTagsCreated()
    {
        if (_tagHandles.Count > 0) return;

        foreach (var tagDef in _config.Tags)
        {
            try
            {
                var tag = CreateTag(tagDef);
                _tagHandles.Add(new TagHandle(tagDef, tag));
                _logger.LogDebug("[{DriverId}] CIP 标签已创建: {Name} ({TagName})", DriverId, tagDef.Name, tagDef.TagName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{DriverId}] 创建 CIP 标签失败: {Name}", DriverId, tagDef.Name);
            }
        }
    }

    private Tag<TPlcMapper, TDotNet> CreateTypedTag<TPlcMapper, TDotNet>(CipTagDefinition tagDef)
        where TPlcMapper : IPlcMapper<TDotNet>, new()
    {
        var tag = new Tag<TPlcMapper, TDotNet>
        {
            Name = tagDef.TagName,
            Gateway = _config.Gateway,
            Path = _config.Path,
            PlcType = ParsePlcType(_config.PlcType),
            Protocol = Protocol.ab_eip,
            Timeout = TimeSpan.FromMilliseconds(_config.TimeoutMs),
        };
        tag.Initialize();
        return tag;
    }

    private ITag CreateTag(CipTagDefinition tagDef)
    {
        return tagDef.DataType.ToUpperInvariant() switch
        {
            "BOOL" => CreateTypedTag<BoolPlcMapper, bool>(tagDef),
            "SINT" => CreateTypedTag<SintPlcMapper, sbyte>(tagDef),
            "INT" => CreateTypedTag<IntPlcMapper, short>(tagDef),
            "DINT" => CreateTypedTag<DintPlcMapper, int>(tagDef),
            "LINT" => CreateTypedTag<LintPlcMapper, long>(tagDef),
            "REAL" => CreateTypedTag<RealPlcMapper, float>(tagDef),
            "LREAL" => CreateTypedTag<LrealPlcMapper, double>(tagDef),
            _ => CreateTypedTag<DintPlcMapper, int>(tagDef) // 默认 DINT
        };
    }

    private async Task ReadAllTagsAsync(CancellationToken ct)
    {
        foreach (var handle in _tagHandles)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await handle.Tag.ReadAsync(ct);
                var value = ReadTagValue(handle);

                await _dataSink.PublishAsync(new DataPoint
                {
                    Tag = $"{DriverId}/{handle.Definition.Name}",
                    Value = value,
                    Timestamp = DateTimeOffset.UtcNow,
                    Quality = DataQuality.Good,
                    SourceDriver = DriverId,
                    Metadata = new Dictionary<string, string>
                    {
                        ["tagName"] = handle.Definition.TagName,
                        ["dataType"] = handle.Definition.DataType,
                        ["protocol"] = "cip"
                    }
                }, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{DriverId}] 读取 CIP 标签 {Name} 失败", DriverId, handle.Definition.Name);

                await _dataSink.PublishAsync(new DataPoint
                {
                    Tag = $"{DriverId}/{handle.Definition.Name}",
                    Value = null,
                    Timestamp = DateTimeOffset.UtcNow,
                    Quality = DataQuality.Bad,
                    SourceDriver = DriverId
                }, ct);
            }
        }
    }

    private static object? ReadTagValue(TagHandle handle)
    {
        return handle.Tag switch
        {
            Tag<BoolPlcMapper, bool> t => t.Value,
            Tag<SintPlcMapper, sbyte> t => t.Value,
            Tag<IntPlcMapper, short> t => t.Value,
            Tag<DintPlcMapper, int> t => t.Value,
            Tag<LintPlcMapper, long> t => t.Value,
            Tag<RealPlcMapper, float> t => t.Value,
            Tag<LrealPlcMapper, double> t => t.Value,
            _ => null
        };
    }

    private void DestroyTags()
    {
        foreach (var handle in _tagHandles)
        {
            try
            {
                handle.Tag.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{DriverId}] 销毁 CIP 标签失败: {Name}", DriverId, handle.Definition.Name);
            }
        }
        _tagHandles.Clear();
    }

    private static PlcType ParsePlcType(string plcType)
    {
        return plcType.ToUpperInvariant() switch
        {
            "CONTROLLOGIX" or "CLX" => PlcType.ControlLogix,
            "COMPACTLOGIX" or "CLGX" => PlcType.ControlLogix,
            "MICRO800" => PlcType.Micro800,
            "MICROLOGIX" => PlcType.MicroLogix,
            "PLC5" => PlcType.Plc5,
            "SLC500" or "SLC" => PlcType.Slc500,
            _ => PlcType.ControlLogix
        };
    }

    private sealed record TagHandle(CipTagDefinition Definition, ITag Tag);
}

internal sealed class CipDriverConfig
{
    public string Gateway { get; set; } = "192.168.1.40";
    public string Path { get; set; } = "1,0";
    public string PlcType { get; set; } = "ControlLogix";
    public int PollIntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 5000;
    public int ReconnectDelayMs { get; set; } = 5000;
    public int LibDebugLevel { get; set; } = 0;
    public List<CipTagDefinition> Tags { get; set; } = [];
}

internal sealed class CipTagDefinition
{
    public string Name { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string DataType { get; set; } = "DINT";
}
