using AmGateway.Abstractions.Models;

namespace AmGateway.Pipeline;

/// <summary>
/// 数据转换引擎接口
/// DataPoint 经过转换后返回新的 DataPoint（替换原始值模式），
/// 返回 null 表示该数据点被过滤丢弃
/// </summary>
public interface ITransformEngine
{
    /// <summary>
    /// 对数据点执行所有匹配的转换规则
    /// </summary>
    /// <returns>转换后的数据点，或 null 表示丢弃</returns>
    DataPoint? Transform(DataPoint point);

    /// <summary>
    /// 加载转换规则
    /// </summary>
    void LoadRules(IReadOnlyList<TransformRule> rules);

    /// <summary>
    /// 获取当前已加载的规则
    /// </summary>
    IReadOnlyList<TransformRule> GetRules();
}
