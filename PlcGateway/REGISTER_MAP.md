# Modbus Register Map

自动生成的寄存器映射文档。请勿手动修改此文件，请更新 `RegisterMap.cs` 后重新生成。

| 地址 (Addr) | 名称 (Name) | 类型 (Type) | 读写 (R/W) | 单位 (Unit) | 缩放/说明 (Scale/Note) | 描述 (Description) |
| :--- | :--- | :--- | :---: | :---: | :--- | :--- |
| 0 | `StatusCoil` | Coil | RW | Boolean | 0=Off, 1=On | 设备运行状态开关 |
| 0 | `Temperature` | HoldingRegister | RO | °C | 寄存器值 / 10.0 | 当前环境温度 |
| 1 | `Pressure` | HoldingRegister | RO | kPa | 寄存器值 / 10.0 | 当前系统压力 |
| 10 | `SimulationMode` | HoldingRegister | RW | Enum | 直接写入枚举整数值 | 模拟器运行模式 (0:Random, 1:Trend, 2:Frozen) |
| 11 | `NoiseMultiplier` | HoldingRegister | RW | Factor | 寄存器值 / 100.0 | 数据波动噪声系数 |
| 12 | `ResponseDelayMs` | HoldingRegister | RW | ms | 直接写入毫秒数 | 模拟响应延迟时间 |
| 100 | `FaultInjectionControl` | HoldingRegister | RW | Enum | 直接写入控制码 | 故障注入控制 (0:正常, 1:异常温度, 2:异常压力, 3:冻结数据) |

## 枚举值参考
### SimulationMode (地址 10)
- `0`: Random (随机波动)
- `1`: Trend (正弦波趋势)
- `2`: Frozen (数值冻结)

### FaultInjectionControl (地址 100)
- `0`: ResumeNormal (恢复正常)
- `1`: FaultyTemperature (异常温度 999.9°C)
- `2`: FaultyPressure (异常压力 -50.0 kPa)
- `3`: FreezeData (冻结数据更新)
