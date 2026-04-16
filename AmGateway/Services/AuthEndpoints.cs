using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AmGateway.Services;

/// <summary>
/// JWT 认证扩展
/// </summary>
public static class AuthEndpoints
{
    /// <summary>
    /// 注册 JWT Bearer 认证服务
    /// </summary>
    public static WebApplicationBuilder AddGatewayJwtAuth(this WebApplicationBuilder builder)
    {
        var jwtSection = builder.Configuration.GetSection("Authentication:Jwt");
        var jwtSettings = jwtSection.Get<JwtAuthSettings>()
            ?? new JwtAuthSettings { SecretKey = "default-dev-key-change-in-production!!" };

        builder.Services.Configure<JwtAuthSettings>(jwtSection);

        if (!jwtSettings.Enabled)
        {
            return builder;
        }

        var key = Encoding.UTF8.GetBytes(jwtSettings.SecretKey);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtSettings.Issuer,
                    ValidAudience = jwtSettings.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            });

        builder.Services.AddAuthorization();

        return builder;
    }

    /// <summary>
    /// 使用认证中间件 + 映射认证端点
    /// </summary>
    public static WebApplication UseGatewayAuth(this WebApplication app)
    {
        var jwtSettings = app.Services.GetRequiredService<IOptions<JwtAuthSettings>>().Value;

        if (!jwtSettings.Enabled)
        {
            app.Logger.LogInformation("[Auth] JWT 认证已禁用");
            return app;
        }

        app.UseAuthentication();
        app.UseAuthorization();

        // 登录端点（免认证）
        app.MapPost("/api/auth/login", (LoginRequest request, IOptions<JwtAuthSettings> options) =>
        {
            var settings = options.Value;

            // 简单用户验证 — 生产环境应替换为数据库/AD 查询
            if (request.Username != "admin" || request.Password != "admin")
            {
                return Results.Unauthorized();
            }

            var token = GenerateToken(request.Username, settings);
            return Results.Ok(new LoginResponse
            {
                Token = token,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(settings.ExpireHours)
            });
        }).AllowAnonymous();

        app.Logger.LogInformation("[Auth] JWT 认证已启用，登录端点: POST /api/auth/login");

        return app;
    }

    private static string GenerateToken(string username, JwtAuthSettings settings)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, "Admin")
        };

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(settings.ExpireHours),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>登录请求</summary>
public sealed class LoginRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
}

/// <summary>登录响应</summary>
public sealed class LoginResponse
{
    public required string Token { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}
