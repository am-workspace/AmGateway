# JWT 认证（AuthEndpoints）待办事项

> 当前完成度约 50%，基础 JWT 签发与验证已闭环，以下为需要改进的问题。

---

## 高优先级

### 1. 硬编码凭据 admin/admin

**现状：** 用户名密码直接写死在代码中。

```csharp
if (request.Username != "admin" || request.Password != "admin")
```

**问题：** 虽然注释写了"生产环境应替换"，但没有防护机制。如果忘记替换，生产环境就是裸奔。

**改动点：** 凭据从配置读取，并要求启动时校验非默认值：

```csharp
// appsettings.json
"Authentication": {
  "Jwt": {
    "Users": [
      { "Username": "admin", "PasswordHash": "sha256..." }
    ]
  }
}

// 启动时检查
if (jwtSettings.SecretKey == "default-dev-key-change-in-production!!")
    throw new InvalidOperationException("生产环境必须更换 JWT 密钥");
```

---

### 2. 密码明文比对，无哈希

**现状：** 无论从哪里读取密码，都是明文比对。

**问题：** 数据库泄露 = 所有用户密码泄露。

**改动点：** 存储密码的 BCrypt / SHA256 哈希值，验证时比对哈希。推荐使用 `BCrypt.Net-Next` 包。

---

### 3. 登录端点无速率限制

**现状：** `POST /api/auth/login` 无任何限流措施。

**问题：** 暴力破解 `admin` 密码毫无阻碍，每秒可尝试上千次。

**改动点：** 加简单的内存限流（每 IP 每分钟最多 N 次失败），可用 `AspNetCoreRateLimit` 包或自实现 `ConcurrentDictionary` 计数。

---

### 4. 默认密钥写在代码中

**现状：** 配置缺失时 fallback 到硬编码默认密钥。

```csharp
?? new JwtAuthSettings { SecretKey = "default-dev-key-change-in-production!!" };
```

**问题：** 如果 `appsettings.json` 缺少配置，这个默认密钥就会用于签发生产 Token。任何人都能用它伪造合法 Token。

**改动点：** 配置缺失时 `throw` 而非 fallback，或仅在 Development 环境允许默认值。

---

## 中优先级

### 5. 所有用户都是 Admin 角色

**现状：** 登录成功一律给 Admin 角色。

```csharp
new Claim(ClaimTypes.Role, "Admin")
```

**问题：** 没有角色区分，API 端点无法做细粒度权限控制。

**改动点：** 角色从用户配置中读取，支持多角色。后续可扩展为基于角色的 API 访问控制。

---

### 6. 无 Token 撤销/黑名单机制

**现状：** JWT 一旦签发，在过期前始终有效。即使用户修改密码、管理员删除账户，旧 Token 仍然可用。

**问题：** 安全事件（如密码泄露、员工离职）后无法即时作废已签发的 Token。

**改动点：** 短期方案——缩短 Token 有效期 + 实现 Refresh Token 机制；长期方案——增加 Token 黑名单（用内存/Redis 存储已撤销的 jti）。

---

### 7. 登录失败无日志记录

**现状：** 验证失败直接返回 401，无任何日志。

```csharp
if (request.Username != "admin" || request.Password != "admin")
{
    return Results.Unauthorized();  // ← 无日志
}
```

**问题：** 无法发现暴力破解攻击或异常登录行为。

**改动点：** 失败时记录 Warning 日志，包含用户名和来源 IP（从 `HttpContext.Connection.RemoteIpAddress` 获取）。

---

### 8. LoginRequest 无输入校验

**现状：** 请求模型只有 `required` 约束，无长度校验。

```csharp
public sealed class LoginRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
}
```

**问题：** 空字符串、超长字符串（如 1MB password）都会直接进入比对逻辑，可能被用于 DoS。

**改动点：** 加 `[MinLength(1)]` / `[MaxLength(256)]` DataAnnotation，或在 handler 中手动校验。

---

## 低优先级

### 9. JWT 配置读取了两次

**现状：** `AddGatewayJwtAuth` 读一次配置注册服务，`UseGatewayAuth` 又读一次检查 `Enabled`。

**问题：** 逻辑分散，两处都维护 `JwtAuthSettings` 的读取。

**改动点：** `AddGatewayJwtAuth` 中将 `Enabled` 也注册到 DI，`UseGatewayAuth` 通过 `IOptions<JwtAuthSettings>` 判断即可（当前已是如此，可进一步合并逻辑）。

---

### 10. SecretKey 长度未校验

**现状：** HMAC-SHA256 要求密钥至少 32 字节，但代码无校验。

**问题：** 如果用户配置了短密钥（如 `"123"`），框架不会报错但安全性严重降低。

**改动点：** `AddGatewayJwtAuth` 中加校验：

```csharp
if (key.Length < 32)
    throw new InvalidOperationException("JWT SecretKey 至少需要 32 字节");
```

---

### 11. 无 Refresh Token 机制

**现状：** 只有 Access Token，过期后必须重新登录。

**问题：** 用户体验差，缩短有效期后更明显。如果实现 Token 撤销（#6），有效期缩短会加剧此问题。

**改动点：** 后续版本增加 Refresh Token 机制——Access Token 短期（如 30min），Refresh Token 长期（如 7天），用 Refresh Token 换新 Access Token。

---

### 12. 全局默认无认证，靠端点组手动启用

**现状：** 认证保护依赖 `ApiEndpoints.cs` 中的 `api.RequireAuthorization()`，登录端点用 `AllowAnonymous()` 豁免。

**问题：** 如果有人忘了 `RequireAuthorization()` 这行，所有端点都会裸露。新增端点时也容易遗漏。

**改动点：** 在 `Program.cs` 全局默认启用认证（如 `app.RequireAuthorization()`），然后登录端点显式 `AllowAnonymous()`，新增端点默认受保护。
