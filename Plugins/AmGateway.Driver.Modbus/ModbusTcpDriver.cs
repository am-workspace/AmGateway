using System.Net.Sockets;
using AmGateway.Abstractions;
using AmGateway.Abstractions.Metadata;
using AmGateway.Abstractions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NModbus;

namespace AmGateway.Driver.Modbus;

/// <summary>
/// Modbus TCP 协议驱动 — 轮询读取 Modbus Server（从站）的寄存器数据，并通过 <see cref="IDataSink"/> 推入网关数据管道。
/// <para>
/// 架构角色：南向协议驱动（IProtocolDriver），只负责数据采集，不涉及北向发布。
/// 与 Publisher（MQTT/InfluxDB）在不同的插件 DLL 中，各自拥有独立的 PluginLoadContext。
/// </para>
/// <para>
/// 数据流向：Modbus 从站 → 本驱动 → IDataSink → Channel 管道 → 转换/路由 → Publisher → 外部系统
/// </para>
/// <para>
/// 核心通信逻辑复用自旧项目 PlcGateway/Services/ModbusClientService.cs
/// </para>
/// </summary>
[DriverMetadata(
    Name = "Modbus TCP",
    ProtocolName = "modbus-tcp",       // 与 appsettings.json 中 Drivers[].Protocol 匹配的标识符
    Version = "1.0.0",
    Description = "Modbus TCP 协议驱动，支持 HoldingRegister / InputRegister / Coil / DiscreteInput 读取")]
public sealed class ModbusTcpDriver : IProtocolDriver
{
    // ───────── 注入的依赖（由主机在 InitializeAsync 阶段提供） ─────────

    /// <summary>日志记录器，由 <see cref="DriverContext.LoggerFactory"/> 创建</summary>
    private ILogger<ModbusTcpDriver> _logger = null!;

    /// <summary>
    /// 数据输出管道 — 驱动通过它将采集到的 <see cref="DataPoint"/> 推入 Channel。
    /// 实际类型是 <c>ChannelDataPipeline.ChannelDataSink</c>，对驱动透明。
    /// </summary>
    private IDataSink _dataSink = null!;

    /// <summary>从 appsettings.json Settings 节绑定而来的驱动配置</summary>
    private ModbusDriverConfig _config = null!;

    // ───────── Modbus 通信资源 ─────────

    /// <summary>TCP 客户端，负责与 Modbus Server 建立底层 TCP 连接</summary>
    private TcpClient? _tcpClient;

    /// <summary>
    /// NModbus Master 实例 — Modbus TCP 主站，用于向从站发起寄存器读写请求。
    /// 工业场景中，网关是 Master（主站），PLC/仪表是 Slave（从站）。
    /// </summary>
    private IModbusMaster? _modbusMaster;

    // ───────── 轮询生命周期管理 ─────────

    /// <summary>链接取消令牌源 — 同时响应外部停止信号和驱动自身停止请求</summary>
    private CancellationTokenSource? _cts;

    /// <summary>后台轮询任务引用，用于 StopAsync 时等待其优雅退出</summary>
    private Task? _pollingTask;

    /// <summary>
    /// 驱动实例唯一标识 — 由主机分配，用于在 DataPoint.Tag 中标识数据来源，
    /// 格式为 "{DriverId}/{寄存器名称}"，例如 "modbus-01/Temperature"
    /// </summary>
    public string DriverId { get; private set; } = string.Empty;

    /// <summary>
    /// 初始化驱动 — 由 <see cref="GatewayRuntime"/> 在创建驱动实例后调用。
    /// <para>
    /// 插件通过 <c>Activator.CreateInstance</c> 实例化，无法使用构造函数 DI，
    /// 所以所有依赖（日志、配置、数据管道）都通过 <see cref="DriverContext"/> 在此阶段注入。
    /// </para>
    /// </summary>
    public Task InitializeAsync(DriverContext context, CancellationToken ct = default)
    {
        DriverId = context.DriverInstanceId;
        _logger = context.LoggerFactory.CreateLogger<ModbusTcpDriver>();
        _dataSink = context.DataSink;

        // 将 IConfiguration 中的 Settings 子节绑定到强类型配置对象
        // 对应 appsettings.json 中 Drivers[].Settings 的内容
        _config = new ModbusDriverConfig();
        context.Configuration.Bind(_config);

        _logger.LogInformation("[{DriverId}] Modbus TCP 驱动已初始化 - 目标: {Host}:{Port}, 从站ID: {SlaveId}, 轮询间隔: {Interval}ms, 寄存器数: {Count}",
            DriverId, _config.Host, _config.Port, _config.SlaveId, _config.PollIntervalMs, _config.Registers.Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 启动数据采集 — 创建链接取消令牌，并在后台启动轮询循环。
    /// <para>
    /// 使用 <c>CreateLinkedTokenSource</c> 将主机的停止令牌与驱动自身的 CTS 关联，
    /// 这样无论是主机优雅关机还是驱动单独停止，都能触发取消。
    /// </para>
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollingTask = RunPollingAsync(_cts.Token);
        _logger.LogInformation("[{DriverId}] Modbus TCP 轮询已启动", DriverId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止数据采集 — 发送取消信号并等待轮询任务退出（最多等 5 秒），然后断开 TCP 连接。
    /// <para>
    /// 超时后仍会继续执行 Disconnect()，避免因轮询任务卡死导致驱动无法停止。
    /// </para>
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        // 向轮询循环发送取消信号
        if (_cts != null)
        {
            await _cts.CancelAsync();
        }

        // 等待轮询任务退出，超时 5 秒后放弃等待
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
                // 正常取消，轮询任务已响应取消信号退出
            }
        }

        Disconnect();
        _logger.LogInformation("[{DriverId}] Modbus TCP 驱动已停止", DriverId);
    }

    /// <summary>
    /// 释放资源 — 断开连接并释放 CTS。
    /// 注意：IProtocolDriver 继承自 IAsyncDisposable，由主机在移除驱动实例时调用。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Disconnect();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 轮询循环主体 — 不断执行 "连接 → 读取 → 等待" 的循环，直到收到取消信号。
    /// <para>
    /// 异常处理策略：
    /// <list type="bullet">
    ///   <item>读取失败 → 断开连接，推送 NotConnected 状态的 DataPoint，等待 ReconnectDelayMs 后重试</item>
    ///   <item>连接异常 → 同上，推送 NotConnected 后等待重连</item>
    ///   <item>外部取消 → 立即退出循环</item>
    /// </list>
    /// </para>
    /// <para>
    /// 注意：读取失败时使用 <c>continue</c> 跳过 PollInterval 等待，立即重试连接，
    /// 这样在断线恢复后可以更快重新采集数据。
    /// </para>
    /// </summary>
    private async Task RunPollingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 确保已连接（首次或断线重连）
                await EnsureConnectedAsync(ct);
                var readOk = await ReadRegistersAsync(ct);

                if (!readOk)
                {
                    // 读取失败，断开连接并等待重连
                    Disconnect();

                    // 向管道推送 NotConnected 状态的 DataPoint，
                    // 让下游（MQTT/InfluxDB）知道该点位当前不可达
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

                    // 等待重连间隔，不立即重试以免频繁连接刷日志
                    try { await Task.Delay(_config.ReconnectDelayMs, ct); }
                    catch (OperationCanceledException) { break; }

                    continue; // 跳过 PollInterval 等待，立即重试连接
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 主机发起的停止，正常退出
                break;
            }
            catch (Exception ex)
            {
                // 连接异常（如网络不可达、拒绝连接等），断开并等待重连
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

            // 本轮读取成功，等待 PollInterval 后进入下一轮
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
    /// 确保 Modbus TCP 连接已建立 — 如果当前未连接则创建新的连接。
    /// <para>
    /// 惰性连接策略：不在 InitializeAsync 时连接，而是在第一次轮询时才建立，
    /// 这样即使目标设备暂时不可达，驱动也不会在初始化阶段就抛异常导致启动失败。
    /// </para>
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_modbusMaster != null) return;

        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(_config.Host, _config.Port, ct);

        // 使用 NModbus 工厂创建 Master 实例
        var factory = new ModbusFactory();
        _modbusMaster = factory.CreateMaster(_tcpClient);

        _logger.LogInformation("[{DriverId}] Modbus 已连接到 {Host}:{Port}", DriverId, _config.Host, _config.Port);
    }

    /// <summary>
    /// 遍历配置中的所有寄存器定义，逐一读取并推送 DataPoint。
    /// <para>
    /// 返回值策略：
    /// <list type="bullet">
    ///   <item><c>true</c> — 全部寄存器读取成功，进入正常 PollInterval 等待</item>
    ///   <item><c>false</c> — 任一寄存器读取失败，触发断连重连流程</item>
    /// </list>
    /// </para>
    /// <para>
    /// 值转换逻辑：
    /// <list type="bullet">
    ///   <item>Coil / DiscreteInput（布尔型）→ 直接使用原始值</item>
    ///   <item>HoldingRegister / InputRegister（数值型）→ 应用 scale * raw + offset 缩放，再四舍五入到指定精度</item>
    /// </list>
    /// 缩放后的值转为 decimal 类型，避免浮点精度问题在下游传播。
    /// </para>
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

                // 根据寄存器类型决定值转换方式
                object value;
                if (register.Type is "Coil" or "DiscreteInput")
                {
                    // 布尔型：直接使用原始值（NModbus 返回 bool）
                    value = rawValue;
                }
                else
                {
                    // 数值型：应用线性缩放 y = scale * x + offset，四舍五入到指定小数位
                    var scaled = Math.Round(Convert.ToDouble(rawValue) * register.Scale + register.Offset, register.Precision);
                    value = (decimal)scaled;
                }

                await _dataSink.PublishAsync(new DataPoint
                {
                    Tag = $"{DriverId}/{register.Name}",       // 格式: "驱动实例ID/寄存器名称"
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
                // 单个寄存器读取失败 → 推送 Bad 状态的 DataPoint，并触发重连
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
                break; // 一个寄存器失败就跳出，不再继续读取后续寄存器
            }
        }

        return allOk;
    }

    /// <summary>
    /// 根据寄存器类型分发到对应的 NModbus 读取方法。
    /// <para>
    /// Modbus TCP 支持四种寄存器区域：
    /// <list type="table">
    ///   <listheader><term>类型</term><description>功能码 | 访问方式</description></listheader>
    ///   <item><term>HoldingRegister</term><description>FC03 | 可读写，保持寄存器</description></item>
    ///   <item><term>InputRegister</term><description>FC04 | 只读，输入寄存器</description></item>
    ///   <item><term>Coil</term><description>FC01 | 可读写，线圈（布尔）</description></item>
    ///   <item><term>DiscreteInput</term><description>FC02 | 只读，离散输入（布尔）</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// 读取结果取第一个元素 [0]，因为通常 Count=1 只读一个寄存器。
    /// 如需批量读取连续寄存器，可配置 Count > 1。
    /// </para>
    /// </summary>
    private async Task<object> ReadRegisterValueAsync(RegisterDefinition register)
    {
        var slaveId = _config.SlaveId;
        var address = (ushort)register.Address;
        var count = (ushort)register.Count;

        return register.Type switch
        {
            "HoldingRegister" => (await _modbusMaster!.ReadHoldingRegistersAsync(slaveId, address, count))[0],
            "InputRegister"   => (await _modbusMaster!.ReadInputRegistersAsync(slaveId, address, count))[0],
            "Coil"            => (await _modbusMaster!.ReadCoilsAsync(slaveId, address, count))[0],
            "DiscreteInput"   => (await _modbusMaster!.ReadInputsAsync(slaveId, address, count))[0],
            _ => throw new NotSupportedException($"不支持的寄存器类型: {register.Type}")
        };
    }

    /// <summary>
    /// 断开 Modbus TCP 连接 — 释放 NModbus Master 和底层 TCP 客户端。
    /// <para>
    /// 注意释放顺序：先 Dispose Master，再关闭并释放 TcpClient。
    /// 将字段置 null 以便 EnsureConnectedAsync 下次重新创建连接。
    /// </para>
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
/// Modbus 驱动内部配置 — 从 appsettings.json 的 Drivers[].Settings 节绑定。
/// <para>
/// 配置示例（appsettings.json）：
/// <code>
/// "Drivers": [{
///   "Protocol": "modbus-tcp",
///   "Settings": {
///     "Host": "192.168.1.10",
///     "Port": 502,
///     "SlaveId": 1,
///     "PollIntervalMs": 1000,
///     "ReconnectDelayMs": 5000,
///     "Registers": [
///       { "Address": 100, "Name": "Temperature", "Type": "HoldingRegister", "Scale": 0.1, "Offset": 0, "Precision": 1 },
///       { "Address": 0, "Name": "Running", "Type": "Coil" }
///     ]
///   }
/// }]
/// </code>
/// </para>
/// </summary>
internal sealed class ModbusDriverConfig
{
    /// <summary>Modbus Server（从站）IP 地址</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Modbus TCP 端口，默认 502（Modbus TCP 标准端口）</summary>
    public int Port { get; set; } = 502;

    /// <summary>
    /// 从站 ID（Slave ID / Unit ID）— Modbus 总线上每台设备的唯一标识。
    /// 在 TCP 网络中通常为 1，在串行总线（RTU/ASCII）中用于寻址不同设备。
    /// </summary>
    public byte SlaveId { get; set; } = 1;

    /// <summary>轮询间隔（毫秒）— 两次读取之间的等待时间</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>重连延迟（毫秒）— 读取失败或连接断开后等待多久再重试</summary>
    public int ReconnectDelayMs { get; set; } = 5000;

    /// <summary>要读取的寄存器列表</summary>
    public List<RegisterDefinition> Registers { get; set; } = [];
}

/// <summary>
/// Modbus 寄存器定义 — 描述一个要采集的数据点在 Modbus 从站上的位置和转换规则。
/// </summary>
internal sealed class RegisterDefinition
{
    /// <summary>
    /// 寄存器起始地址（0-based）。
    /// 注意：PLC 地址通常是 1-based（如 MW100 对应地址 50），
    /// 而此处应填 Modbus 协议层的 0-based 地址。
    /// </summary>
    public int Address { get; set; }

    /// <summary>
    /// 数据点名称 — 用于组成 DataPoint.Tag，格式为 "{DriverId}/{Name}"。
    /// 应使用有意义的名称，如 "Temperature"、"Pressure" 等。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 寄存器类型 — 决定使用哪个 Modbus 功能码读取：
    /// <list type="bullet">
    ///   <item><c>HoldingRegister</c> — FC03，可读写保持寄存器（最常用）</item>
    ///   <item><c>InputRegister</c> — FC04，只读输入寄存器</item>
    ///   <item><c>Coil</c> — FC01，可读写线圈（布尔量）</item>
    ///   <item><c>DiscreteInput</c> — FC02，只读离散输入（布尔量）</item>
    /// </list>
    /// </summary>
    public string Type { get; set; } = "HoldingRegister";

    /// <summary>
    /// 连续读取的寄存器数量 — 默认 1。
    /// 大于 1 时读取从 Address 开始的连续 Count 个寄存器，仅取第一个值。
    /// </summary>
    public int Count { get; set; } = 1;

    /// <summary>
    /// 缩放系数 — 读取值乘以此系数。公式：y = Scale * x + Offset。
    /// 典型用途：PLC 存储整数但实际值需要小数，如温度存为 235 表示 23.5°C，Scale = 0.1。
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// 偏移量 — 缩放后加上此值。公式：y = Scale * x + Offset。
    /// 典型用途：PLC 存储的是相对值，需要加偏移转换为绝对值。
    /// </summary>
    public double Offset { get; set; } = 0.0;

    /// <summary>
    /// 小数精度 — 缩放计算结果四舍五入到的小数位数，默认 2。
    /// 仅对 HoldingRegister 和 InputRegister 类型生效。
    /// </summary>
    public int Precision { get; set; } = 2;
}
