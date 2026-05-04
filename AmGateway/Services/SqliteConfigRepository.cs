using System.Text.Json;
using AmGateway.Abstractions.Models;
using Microsoft.Data.Sqlite;

namespace AmGateway.Services;

/// <summary>
/// 基于 SQLite 的配置持久化仓库
/// </summary>
public sealed class SqliteConfigRepository : IConfigRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteConfigRepository> _logger;
    private bool _initialized;

    /// <summary>
    /// 初始化 SQLite 配置仓库
    /// </summary>
    /// <param name="logger">日志记录器</param>
    /// <param name="dbPath">数据库文件路径，默认为程序目录下的 data/amgateway.db</param>
    public SqliteConfigRepository(ILogger<SqliteConfigRepository> logger, string? dbPath = null)
    {
        _logger = logger;
        dbPath ??= Path.Combine(AppContext.BaseDirectory, "data", "amgateway.db");
        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={dbPath}";
    }

    /// <summary>
    /// 确保数据库已初始化，首次调用时会创建所需的表结构
    /// </summary>
    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS drivers (
                instance_id TEXT PRIMARY KEY,
                protocol TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                settings_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS publishers (
                instance_id TEXT PRIMARY KEY,
                transport TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                settings_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS transform_rules (
                rule_id TEXT PRIMARY KEY,
                enabled INTEGER NOT NULL DEFAULT 1,
                tag_pattern TEXT NOT NULL,
                type TEXT NOT NULL,
                parameters_json TEXT NOT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS route_rules (
                rule_id TEXT PRIMARY KEY,
                enabled INTEGER NOT NULL DEFAULT 1,
                tag_pattern TEXT NOT NULL,
                target_publisher_ids_json TEXT NOT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();

        _initialized = true;
        _logger.LogInformation("[ConfigRepo] SQLite 数据库已初始化: {Path}", _connectionString);
    }

    // === 驱动 ===

    /// <summary>
    /// 获取所有驱动配置记录
    /// </summary>
    /// <returns>驱动配置记录只读列表，按创建时间排序</returns>
    public async Task<IReadOnlyList<DriverConfigRecord>> GetAllDriversAsync()
    {
        await EnsureInitializedAsync();
        var list = new List<DriverConfigRecord>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instance_id, protocol, enabled, settings_json, created_at, updated_at FROM drivers ORDER BY created_at";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadDriverRecord(reader));
        }

        return list;
    }

    /// <summary>
    /// 根据实例 ID 获取单个驱动配置记录
    /// </summary>
    /// <param name="instanceId">驱动实例标识符</param>
    /// <returns>驱动配置记录；不存在时返回 null</returns>
    public async Task<DriverConfigRecord?> GetDriverAsync(string instanceId)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instance_id, protocol, enabled, settings_json, created_at, updated_at FROM drivers WHERE instance_id = @id";
        cmd.Parameters.AddWithValue("@id", instanceId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadDriverRecord(reader);
        }

        return null;
    }

    /// <summary>
    /// 保存驱动配置记录，实例 ID 已存在时执行更新操作
    /// </summary>
    /// <param name="record">驱动配置记录</param>
    public async Task SaveDriverAsync(DriverConfigRecord record)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO drivers (instance_id, protocol, enabled, settings_json, created_at, updated_at)
            VALUES (@id, @proto, @enabled, @json, @created, @updated)
            ON CONFLICT(instance_id) DO UPDATE SET
                protocol = @proto,
                enabled = @enabled,
                settings_json = @json,
                updated_at = @updated
            """;
        cmd.Parameters.AddWithValue("@id", record.InstanceId);
        cmd.Parameters.AddWithValue("@proto", record.Protocol);
        cmd.Parameters.AddWithValue("@enabled", record.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@json", record.SettingsJson);
        cmd.Parameters.AddWithValue("@created", record.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated", record.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("[ConfigRepo] 已保存驱动配置: {InstanceId}", record.InstanceId);
    }

    /// <summary>
    /// 根据实例 ID 删除驱动配置记录
    /// </summary>
    /// <param name="instanceId">驱动实例标识符</param>
    public async Task DeleteDriverAsync(string instanceId)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM drivers WHERE instance_id = @id";
        cmd.Parameters.AddWithValue("@id", instanceId);

        await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("[ConfigRepo] 已删除驱动配置: {InstanceId}", instanceId);
    }

    // === 发布器 ===

    /// <summary>
    /// 获取所有发布器配置记录
    /// </summary>
    /// <returns>发布器配置记录只读列表，按创建时间排序</returns>
    public async Task<IReadOnlyList<PublisherConfigRecord>> GetAllPublishersAsync()
    {
        await EnsureInitializedAsync();
        var list = new List<PublisherConfigRecord>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instance_id, transport, enabled, settings_json, created_at, updated_at FROM publishers ORDER BY created_at";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadPublisherRecord(reader));
        }

        return list;
    }

    /// <summary>
    /// 根据实例 ID 获取单个发布器配置记录
    /// </summary>
    /// <param name="instanceId">发布器实例标识符</param>
    /// <returns>发布器配置记录；不存在时返回 null</returns>
    public async Task<PublisherConfigRecord?> GetPublisherAsync(string instanceId)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instance_id, transport, enabled, settings_json, created_at, updated_at FROM publishers WHERE instance_id = @id";
        cmd.Parameters.AddWithValue("@id", instanceId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadPublisherRecord(reader);
        }

        return null;
    }

    /// <summary>
    /// 保存发布器配置记录，实例 ID 已存在时执行更新操作
    /// </summary>
    /// <param name="record">发布器配置记录</param>
    public async Task SavePublisherAsync(PublisherConfigRecord record)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO publishers (instance_id, transport, enabled, settings_json, created_at, updated_at)
            VALUES (@id, @transport, @enabled, @json, @created, @updated)
            ON CONFLICT(instance_id) DO UPDATE SET
                transport = @transport,
                enabled = @enabled,
                settings_json = @json,
                updated_at = @updated
            """;
        cmd.Parameters.AddWithValue("@id", record.InstanceId);
        cmd.Parameters.AddWithValue("@transport", record.Transport);
        cmd.Parameters.AddWithValue("@enabled", record.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@json", record.SettingsJson);
        cmd.Parameters.AddWithValue("@created", record.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated", record.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("[ConfigRepo] 已保存发布器配置: {InstanceId}", record.InstanceId);
    }

    /// <summary>
    /// 根据实例 ID 删除发布器配置记录
    /// </summary>
    /// <param name="instanceId">发布器实例标识符</param>
    public async Task DeletePublisherAsync(string instanceId)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM publishers WHERE instance_id = @id";
        cmd.Parameters.AddWithValue("@id", instanceId);

        await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("[ConfigRepo] 已删除发布器配置: {InstanceId}", instanceId);
    }

    // === 转换规则 ===

    /// <summary>
    /// 获取所有转换规则记录
    /// </summary>
    /// <returns>转换规则记录只读列表，按优先级和创建时间排序</returns>
    public async Task<IReadOnlyList<TransformRuleRecord>> GetAllTransformRulesAsync()
    {
        await EnsureInitializedAsync();
        var list = new List<TransformRuleRecord>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rule_id, enabled, tag_pattern, type, parameters_json, priority, created_at, updated_at FROM transform_rules ORDER BY priority, created_at";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadTransformRuleRecord(reader));
        }

        return list;
    }

    /// <summary>
    /// 根据规则 ID 获取单个转换规则记录
    /// </summary>
    /// <param name="ruleId">转换规则标识符</param>
    /// <returns>转换规则记录；不存在时返回 null</returns>
    public async Task<TransformRuleRecord?> GetTransformRuleAsync(string ruleId)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rule_id, enabled, tag_pattern, type, parameters_json, priority, created_at, updated_at FROM transform_rules WHERE rule_id = @id";
        cmd.Parameters.AddWithValue("@id", ruleId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadTransformRuleRecord(reader);
        }

        return null;
    }

    /// <summary>
    /// 保存转换规则记录，规则 ID 已存在时执行更新操作
    /// </summary>
    /// <param name="record">转换规则记录</param>
    public async Task SaveTransformRuleAsync(TransformRuleRecord record)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO transform_rules (rule_id, enabled, tag_pattern, type, parameters_json, priority, created_at, updated_at)
            VALUES (@id, @enabled, @pattern, @type, @json, @priority, @created, @updated)
            ON CONFLICT(rule_id) DO UPDATE SET
                enabled = @enabled,
                tag_pattern = @pattern,
                type = @type,
                parameters_json = @json,
                priority = @priority,
                updated_at = @updated
            """;
        cmd.Parameters.AddWithValue("@id", record.RuleId);
        cmd.Parameters.AddWithValue("@enabled", record.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@pattern", record.TagPattern);
        cmd.Parameters.AddWithValue("@type", record.Type.ToString());
        cmd.Parameters.AddWithValue("@json", record.ParametersJson);
        cmd.Parameters.AddWithValue("@priority", record.Priority);
        cmd.Parameters.AddWithValue("@created", record.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated", record.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("[ConfigRepo] 已保存转换规则: {RuleId}", record.RuleId);
    }

    /// <summary>
    /// 根据规则 ID 删除转换规则记录
    /// </summary>
    /// <param name="ruleId">转换规则标识符</param>
    public async Task DeleteTransformRuleAsync(string ruleId)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM transform_rules WHERE rule_id = @id";
        cmd.Parameters.AddWithValue("@id", ruleId);

        await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("[ConfigRepo] 已删除转换规则: {RuleId}", ruleId);
    }

    // === 路由规则 ===

    /// <summary>
    /// 获取所有路由规则记录
    /// </summary>
    /// <returns>路由规则记录只读列表，按优先级和创建时间排序</returns>
    public async Task<IReadOnlyList<RouteRuleRecord>> GetAllRouteRulesAsync()
    {
        await EnsureInitializedAsync();
        var list = new List<RouteRuleRecord>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rule_id, enabled, tag_pattern, target_publisher_ids_json, priority, created_at, updated_at FROM route_rules ORDER BY priority, created_at";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadRouteRuleRecord(reader));
        }

        return list;
    }

    /// <summary>
    /// 根据规则 ID 获取单个路由规则记录
    /// </summary>
    /// <param name="ruleId">路由规则标识符</param>
    /// <returns>路由规则记录；不存在时返回 null</returns>
    public async Task<RouteRuleRecord?> GetRouteRuleAsync(string ruleId)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rule_id, enabled, tag_pattern, target_publisher_ids_json, priority, created_at, updated_at FROM route_rules WHERE rule_id = @id";
        cmd.Parameters.AddWithValue("@id", ruleId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadRouteRuleRecord(reader);
        }

        return null;
    }

    /// <summary>
    /// 保存路由规则记录，规则 ID 已存在时执行更新操作
    /// </summary>
    /// <param name="record">路由规则记录</param>
    public async Task SaveRouteRuleAsync(RouteRuleRecord record)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO route_rules (rule_id, enabled, tag_pattern, target_publisher_ids_json, priority, created_at, updated_at)
            VALUES (@id, @enabled, @pattern, @json, @priority, @created, @updated)
            ON CONFLICT(rule_id) DO UPDATE SET
                enabled = @enabled,
                tag_pattern = @pattern,
                target_publisher_ids_json = @json,
                priority = @priority,
                updated_at = @updated
            """;
        cmd.Parameters.AddWithValue("@id", record.RuleId);
        cmd.Parameters.AddWithValue("@enabled", record.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@pattern", record.TagPattern);
        cmd.Parameters.AddWithValue("@json", record.TargetPublisherIdsJson);
        cmd.Parameters.AddWithValue("@priority", record.Priority);
        cmd.Parameters.AddWithValue("@created", record.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated", record.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("[ConfigRepo] 已保存路由规则: {RuleId}", record.RuleId);
    }

    /// <summary>
    /// 根据规则 ID 删除路由规则记录
    /// </summary>
    /// <param name="ruleId">路由规则标识符</param>
    public async Task DeleteRouteRuleAsync(string ruleId)
    {
        await EnsureInitializedAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM route_rules WHERE rule_id = @id";
        cmd.Parameters.AddWithValue("@id", ruleId);

        await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("[ConfigRepo] 已删除路由规则: {RuleId}", ruleId);
    }

    // === JSON 导出/导入 ===

    /// <summary>
    /// 将所有配置（驱动、发布器、转换规则、路由规则）导出为 JSON 字符串
    /// </summary>
    /// <returns>格式化后的 JSON 字符串</returns>
    public async Task<string> ExportToJsonAsync()
    {
        var drivers = await GetAllDriversAsync();
        var publishers = await GetAllPublishersAsync();
        var transformRules = await GetAllTransformRulesAsync();
        var routeRules = await GetAllRouteRulesAsync();

        var export = new
        {
            drivers = drivers.Select(d => new
            {
                d.InstanceId,
                d.Protocol,
                d.Enabled,
                Settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(d.SettingsJson),
                d.CreatedAt,
                d.UpdatedAt
            }),
            publishers = publishers.Select(p => new
            {
                p.InstanceId,
                p.Transport,
                p.Enabled,
                Settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(p.SettingsJson),
                p.CreatedAt,
                p.UpdatedAt
            }),
            transformRules = transformRules.Select(t => new
            {
                t.RuleId,
                t.Enabled,
                t.TagPattern,
                Type = t.Type.ToString(),
                Parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(t.ParametersJson),
                t.Priority,
                t.CreatedAt,
                t.UpdatedAt
            }),
            routeRules = routeRules.Select(r => new
            {
                r.RuleId,
                r.Enabled,
                r.TagPattern,
                TargetPublisherIds = JsonSerializer.Deserialize<List<string>>(r.TargetPublisherIdsJson),
                r.Priority,
                r.CreatedAt,
                r.UpdatedAt
            })
        };

        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 从 JSON 字符串导入配置，覆盖或新增驱动、发布器、转换规则和路由规则
    /// </summary>
    /// <param name="json">包含完整配置的 JSON 字符串</param>
    public async Task ImportFromJsonAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("drivers", out var driversEl))
        {
            foreach (var d in driversEl.EnumerateArray())
            {
                var record = new DriverConfigRecord
                {
                    InstanceId = d.GetProperty("InstanceId").GetString()!,
                    Protocol = d.GetProperty("Protocol").GetString()!,
                    Enabled = d.TryGetProperty("Enabled", out var en) ? en.GetBoolean() : true,
                    SettingsJson = d.GetProperty("Settings").GetRawText(),
                    CreatedAt = d.TryGetProperty("CreatedAt", out var ca) ? DateTimeOffset.Parse(ca.GetString()!) : DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await SaveDriverAsync(record);
            }
        }

        if (root.TryGetProperty("publishers", out var publishersEl))
        {
            foreach (var p in publishersEl.EnumerateArray())
            {
                var record = new PublisherConfigRecord
                {
                    InstanceId = p.GetProperty("InstanceId").GetString()!,
                    Transport = p.GetProperty("Transport").GetString()!,
                    Enabled = p.TryGetProperty("Enabled", out var en) ? en.GetBoolean() : true,
                    SettingsJson = p.GetProperty("Settings").GetRawText(),
                    CreatedAt = p.TryGetProperty("CreatedAt", out var ca) ? DateTimeOffset.Parse(ca.GetString()!) : DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await SavePublisherAsync(record);
            }
        }

        if (root.TryGetProperty("transformRules", out var transformRulesEl))
        {
            foreach (var t in transformRulesEl.EnumerateArray())
            {
                var typeStr = t.GetProperty("Type").GetString()!;
                var record = new TransformRuleRecord
                {
                    RuleId = t.GetProperty("RuleId").GetString()!,
                    Enabled = t.TryGetProperty("Enabled", out var en) ? en.GetBoolean() : true,
                    TagPattern = t.GetProperty("TagPattern").GetString()!,
                    Type = Enum.Parse<TransformType>(typeStr, true),
                    ParametersJson = t.GetProperty("Parameters").GetRawText(),
                    Priority = t.TryGetProperty("Priority", out var pr) ? pr.GetInt32() : 100,
                    CreatedAt = t.TryGetProperty("CreatedAt", out var ca) ? DateTimeOffset.Parse(ca.GetString()!) : DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await SaveTransformRuleAsync(record);
            }
        }

        if (root.TryGetProperty("routeRules", out var routeRulesEl))
        {
            foreach (var r in routeRulesEl.EnumerateArray())
            {
                var record = new RouteRuleRecord
                {
                    RuleId = r.GetProperty("RuleId").GetString()!,
                    Enabled = r.TryGetProperty("Enabled", out var en) ? en.GetBoolean() : true,
                    TagPattern = r.GetProperty("TagPattern").GetString()!,
                    TargetPublisherIdsJson = r.GetProperty("TargetPublisherIds").GetRawText(),
                    Priority = r.TryGetProperty("Priority", out var pr) ? pr.GetInt32() : 100,
                    CreatedAt = r.TryGetProperty("CreatedAt", out var ca) ? DateTimeOffset.Parse(ca.GetString()!) : DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await SaveRouteRuleAsync(record);
            }
        }

        _logger.LogInformation("[ConfigRepo] JSON 导入完成");
    }

    /// <summary>
    /// 释放资源（SQLite 连接每次操作后已释放，此处无需额外清理）
    /// </summary>
    public void Dispose()
    {
        // SQLite 连接每次操作后已释放，无需额外清理
    }

    /// <summary>
    /// 从数据读取器中解析驱动配置记录
    /// </summary>
    /// <param name="reader">SQLite 数据读取器</param>
    /// <returns>驱动配置记录</returns>
    private static DriverConfigRecord ReadDriverRecord(SqliteDataReader reader)
    {
        return new DriverConfigRecord
        {
            InstanceId = reader.GetString(0),
            Protocol = reader.GetString(1),
            Enabled = reader.GetInt32(2) == 1,
            SettingsJson = reader.GetString(3),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
        };
    }

    /// <summary>
    /// 从数据读取器中解析发布器配置记录
    /// </summary>
    /// <param name="reader">SQLite 数据读取器</param>
    /// <returns>发布器配置记录</returns>
    private static PublisherConfigRecord ReadPublisherRecord(SqliteDataReader reader)
    {
        return new PublisherConfigRecord
        {
            InstanceId = reader.GetString(0),
            Transport = reader.GetString(1),
            Enabled = reader.GetInt32(2) == 1,
            SettingsJson = reader.GetString(3),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
        };
    }

    /// <summary>
    /// 从数据读取器中解析转换规则记录
    /// </summary>
    /// <param name="reader">SQLite 数据读取器</param>
    /// <returns>转换规则记录</returns>
    private static TransformRuleRecord ReadTransformRuleRecord(SqliteDataReader reader)
    {
        return new TransformRuleRecord
        {
            RuleId = reader.GetString(0),
            Enabled = reader.GetInt32(1) == 1,
            TagPattern = reader.GetString(2),
            Type = Enum.Parse<TransformType>(reader.GetString(3), true),
            ParametersJson = reader.GetString(4),
            Priority = reader.GetInt32(5),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(6)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(7))
        };
    }

    /// <summary>
    /// 从数据读取器中解析路由规则记录
    /// </summary>
    /// <param name="reader">SQLite 数据读取器</param>
    /// <returns>路由规则记录</returns>
    private static RouteRuleRecord ReadRouteRuleRecord(SqliteDataReader reader)
    {
        return new RouteRuleRecord
        {
            RuleId = reader.GetString(0),
            Enabled = reader.GetInt32(1) == 1,
            TagPattern = reader.GetString(2),
            TargetPublisherIdsJson = reader.GetString(3),
            Priority = reader.GetInt32(4),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(6))
        };
    }
}
