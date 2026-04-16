using System.Text.Json;
using System.Text.RegularExpressions;
using AmGateway.Abstractions.Models;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Microsoft.Extensions.Logging;

namespace AmGateway.Pipeline;

/// <summary>
/// 数据转换引擎实现
/// 支持：线性缩放、分段线性、单位换算、死区过滤、JavaScript 脚本
/// </summary>
public sealed class TransformEngine : ITransformEngine
{
    private readonly ILogger<TransformEngine> _logger;
    private readonly Dictionary<string, TransformRuleEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    // 死区过滤需要记录上一次的值
    private readonly Dictionary<string, object?> _lastValues = new(StringComparer.OrdinalIgnoreCase);

    public TransformEngine(ILogger<TransformEngine> logger)
    {
        _logger = logger;
    }

    public DataPoint? Transform(DataPoint point)
    {
        List<TransformRuleEntry> matched;
        lock (_lock)
        {
            matched = _entries.Values
                .Where(e => e.Rule.Enabled && IsTagMatch(point.Tag, e.Rule.TagPattern))
                .OrderBy(e => e.Rule.Priority)
                .ToList();
        }

        if (matched.Count == 0)
            return point; // 无匹配规则，原样返回

        DataPoint? current = point;
        foreach (var entry in matched)
        {
            if (current == null)
                return null; // 已被过滤

            try
            {
                current = ApplyRule(current.Value, entry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Transform] 规则 {RuleId} 执行失败，Tag={Tag}", entry.Rule.RuleId, point.Tag);
                // 规则执行失败时保持当前值，不丢弃
            }
        }

        return current;
    }

    public void LoadRules(IReadOnlyList<TransformRule> rules)
    {
        lock (_lock)
        {
            _entries.Clear();
            _lastValues.Clear();
            foreach (var rule in rules)
            {
                var entry = CreateEntry(rule);
                _entries[rule.RuleId] = entry;
            }
        }
        _logger.LogInformation("[TransformEngine] 已加载 {Count} 条转换规则", rules.Count);
    }

    public IReadOnlyList<TransformRule> GetRules()
    {
        lock (_lock)
        {
            return _entries.Values.Select(e => e.Rule).ToList();
        }
    }

    private DataPoint? ApplyRule(DataPoint point, TransformRuleEntry entry)
    {
        return entry.Rule.Type switch
        {
            TransformType.Linear => ApplyLinear(point, entry),
            TransformType.PiecewiseLinear => ApplyPiecewiseLinear(point, entry),
            TransformType.UnitConversion => ApplyUnitConversion(point, entry),
            TransformType.Deadband => ApplyDeadband(point, entry),
            TransformType.Script => ApplyScript(point, entry),
            _ => point
        };
    }

    #region 线性缩放 y = kx + b

    private DataPoint? ApplyLinear(DataPoint point, TransformRuleEntry entry)
    {
        var p = entry.Parameters;
        var k = GetDouble(p, "k", 1.0);
        var b = GetDouble(p, "b", 0.0);

        if (point.Value is decimal dec)
            return point with { Value = (decimal)k * dec + (decimal)b };
        if (point.Value is double d)
            return point with { Value = k * d + b };
        if (point.Value is float f)
            return point with { Value = k * f + b };
        if (point.Value is int i)
            return point with { Value = k * i + b };
        if (point.Value is long l)
            return point with { Value = k * l + b };
        if (point.Value is short s)
            return point with { Value = k * s + b };
        if (point.Value is ushort us)
            return point with { Value = k * us + b };
        if (point.Value is byte bt)
            return point with { Value = k * bt + b };

        _logger.LogWarning("[Transform] 线性缩放规则 {RuleId} 无法处理值类型 {Type}", entry.Rule.RuleId, point.Value?.GetType().Name);
        return point;
    }

    #endregion

    #region 分段线性

    private DataPoint? ApplyPiecewiseLinear(DataPoint point, TransformRuleEntry entry)
    {
        var p = entry.Parameters;
        if (!p.TryGetValue("Points", out var pointsObj) || pointsObj is not JsonElement pointsEl)
        {
            _logger.LogWarning("[Transform] 分段线性规则 {RuleId} 缺少 Points 参数", entry.Rule.RuleId);
            return point;
        }

        var points = new List<(double X, double Y)>();
        foreach (var item in pointsEl.EnumerateArray())
        {
            if (item.GetArrayLength() >= 2)
                points.Add((item[0].GetDouble(), item[1].GetDouble()));
        }

        if (points.Count < 2)
        {
            _logger.LogWarning("[Transform] 分段线性规则 {RuleId} 至少需要 2 个点", entry.Rule.RuleId);
            return point;
        }

        points.Sort((a, b) => a.X.CompareTo(b.X));

        var inputVal = Convert.ToDouble(point.Value);

        // 外推：低于最低点
        if (inputVal <= points[0].X)
            return point with { Value = points[0].Y };

        // 外推：高于最高点
        if (inputVal >= points[^1].X)
            return point with { Value = points[^1].Y };

        // 线性插值
        for (var i = 0; i < points.Count - 1; i++)
        {
            if (inputVal >= points[i].X && inputVal <= points[i + 1].X)
            {
                var ratio = (inputVal - points[i].X) / (points[i + 1].X - points[i].X);
                return point with { Value = points[i].Y + ratio * (points[i + 1].Y - points[i].Y) };
            }
        }

        return point;
    }

    #endregion

    #region 单位换算

    private DataPoint? ApplyUnitConversion(DataPoint point, TransformRuleEntry entry)
    {
        var p = entry.Parameters;
        var from = GetString(p, "From", "");
        var to = GetString(p, "To", "");

        var inputVal = Convert.ToDouble(point.Value);
        var result = UnitConverter.Convert(inputVal, from, to);

        if (result.HasValue)
            return point with { Value = result.Value };

        _logger.LogWarning("[Transform] 单位换算规则 {RuleId} 不支持 {From} → {To}", entry.Rule.RuleId, from, to);
        return point;
    }

    #endregion

    #region 死区过滤

    private DataPoint? ApplyDeadband(DataPoint point, TransformRuleEntry entry)
    {
        var p = entry.Parameters;
        var threshold = GetDouble(p, "Threshold", 0.0);
        var mode = GetString(p, "Mode", "absolute"); // absolute | percent

        if (threshold <= 0)
            return point;

        var currentVal = Convert.ToDouble(point.Value);

        lock (_lastValues)
        {
            if (_lastValues.TryGetValue(point.Tag, out var lastObj))
            {
                var lastVal = Convert.ToDouble(lastObj);
                var diff = Math.Abs(currentVal - lastVal);

                var shouldFilter = mode == "percent" && lastVal != 0
                    ? diff / Math.Abs(lastVal) * 100.0 < threshold
                    : diff < threshold;

                if (shouldFilter)
                    return null; // 值变化在死区内，丢弃
            }

            _lastValues[point.Tag] = point.Value;
        }

        return point;
    }

    #endregion

    #region JavaScript 脚本

    private DataPoint? ApplyScript(DataPoint point, TransformRuleEntry entry)
    {
        var p = entry.Parameters;
        var script = GetString(p, "Script", "");

        if (string.IsNullOrEmpty(script))
        {
            _logger.LogWarning("[Transform] 脚本规则 {RuleId} 缺少 Script 参数", entry.Rule.RuleId);
            return point;
        }

        var engine = entry.GetOrCreateEngine();

        engine.SetValue("value", point.Value);
        engine.SetValue("tag", point.Tag);
        engine.SetValue("quality", point.Quality.ToString());
        engine.SetValue("timestamp", point.Timestamp.ToString("O"));
        engine.SetValue("sourceDriver", point.SourceDriver);

        var result = engine.Evaluate(script);

        if (result == null || result.IsUndefined())
            return null; // 脚本返回 undefined = 丢弃

        if (result.IsBoolean())
            return result.AsBoolean() ? point : null; // 返回 bool = 过滤条件

        if (result.IsNumber())
            return point with { Value = result.AsNumber() };

        if (result.IsString())
            return point with { Value = result.AsString() };

        return point;
    }

    #endregion

    #region 辅助方法

    private static TransformRuleEntry CreateEntry(TransformRule rule)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(rule.ParametersJson))
        {
            using var doc = JsonDocument.Parse(rule.ParametersJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                parameters[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Array => prop.Value.GetRawText(),
                    JsonValueKind.Object => prop.Value.GetRawText(),
                    _ => null
                };
            }
        }

        return new TransformRuleEntry(rule, parameters);
    }

    internal static bool IsTagMatch(string tag, string pattern)
    {
        if (pattern == "*" || pattern == "**")
            return true;

        // 精确匹配
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(tag, pattern, StringComparison.OrdinalIgnoreCase);

        // Glob 匹配
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(tag, regexPattern, RegexOptions.IgnoreCase);
    }

    private static double GetDouble(Dictionary<string, object?> dict, string key, double defaultValue)
    {
        if (!dict.TryGetValue(key, out var val) || val == null)
            return defaultValue;
        return val is double d ? d : Convert.ToDouble(val);
    }

    private static string GetString(Dictionary<string, object?> dict, string key, string defaultValue)
    {
        if (!dict.TryGetValue(key, out var val) || val == null)
            return defaultValue;
        return val.ToString() ?? defaultValue;
    }

    #endregion

    /// <summary>转换规则内部条目（包含预编译的 Jint 引擎）</summary>
    private sealed class TransformRuleEntry
    {
        public TransformRule Rule { get; }
        public Dictionary<string, object?> Parameters { get; }
        private Engine? _engine;

        public TransformRuleEntry(TransformRule rule, Dictionary<string, object?> parameters)
        {
            Rule = rule;
            Parameters = parameters;
        }

        public Engine GetOrCreateEngine()
        {
            if (_engine != null) return _engine;

            _engine = new Engine(options =>
            {
                options.MaxStatements(10000);
                options.TimeoutInterval(TimeSpan.FromSeconds(5));
            });

            return _engine;
        }
    }
}

/// <summary>
/// 单位换算工具
/// </summary>
internal static class UnitConverter
{
    private static readonly Dictionary<(string From, string To), Func<double, double>> Conversions = [];

    static UnitConverter()
    {
        // 温度
        Add("C", "F", v => v * 9 / 5 + 32);
        Add("F", "C", v => (v - 32) * 5 / 9);
        Add("C", "K", v => v + 273.15);
        Add("K", "C", v => v - 273.15);
        Add("F", "K", v => (v - 32) * 5 / 9 + 273.15);
        Add("K", "F", v => (v - 273.15) * 9 / 5 + 32);
        // 压力
        Add("Pa", "kPa", v => v / 1000);
        Add("kPa", "Pa", v => v * 1000);
        Add("Pa", "MPa", v => v / 1e6);
        Add("MPa", "Pa", v => v * 1e6);
        Add("bar", "Pa", v => v * 100000);
        Add("Pa", "bar", v => v / 100000);
        Add("bar", "kPa", v => v * 100);
        Add("kPa", "bar", v => v / 100);
        Add("psi", "kPa", v => v * 6.89476);
        Add("kPa", "psi", v => v / 6.89476);
        Add("psi", "bar", v => v * 0.0689476);
        Add("bar", "psi", v => v / 0.0689476);
        // 长度
        Add("mm", "m", v => v / 1000);
        Add("m", "mm", v => v * 1000);
        Add("in", "mm", v => v * 25.4);
        Add("mm", "in", v => v / 25.4);
        Add("ft", "m", v => v * 0.3048);
        Add("m", "ft", v => v / 0.3048);
        // 流量
        Add("L/min", "m³/h", v => v * 0.06);
        Add("m³/h", "L/min", v => v / 0.06);
        Add("gpm", "L/min", v => v * 3.78541);
        Add("L/min", "gpm", v => v / 3.78541);
    }

    private static void Add(string from, string to, Func<double, double> converter)
    {
        Conversions[(from, to)] = converter;
    }

    public static double? Convert(double value, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return value;

        return Conversions.TryGetValue((from, to), out var converter) ? converter(value) : null;
    }
}
