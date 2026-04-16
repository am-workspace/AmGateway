namespace PlcGateway.Configuration;

/// <summary>
/// Modbus 客户端配置
/// </summary>
public class ModbusSettings
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5020;
    public int PollIntervalMs { get; set; } = 1000;
    public List<ModbusRegister> Registers { get; set; } = new();
}

/// <summary>
/// Modbus 寄存器定义
/// </summary>
public class ModbusRegister
{
    public int Address { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "HoldingRegister";
}

/// <summary>
/// OPC UA 客户端配置
/// </summary>
public class OpcUaSettings
{
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = "opc.tcp://localhost:4840";
    public List<OpcUaNode> Nodes { get; set; } = new();
}

/// <summary>
/// OPC UA 节点定义
/// </summary>
public class OpcUaNode
{
    public string NodeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// MQTT 发布配置
/// </summary>
public class MqttSettings
{
    public bool Enabled { get; set; } = true;
    public string Broker { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string TopicPrefix { get; set; } = "industrial";
    public string ClientId { get; set; } = "PlcGateway";
    public string? Username { get; set; }
    public string? Password { get; set; }
}
