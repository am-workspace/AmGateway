using System.Collections.Concurrent;
using AmGateway.Abstractions;
using AmGateway.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace AmGateway.Services;

/// <summary>
/// 驱动看门狗 — 监控驱动运行状态，异常时自动重启（有限重试 + 指数退避）
/// </summary>
public sealed class DriverWatchdog : IDisposable
{
    private readonly string _instanceId;
    private readonly IProtocolDriver _driver;
    private readonly GatewayRuntime _runtime;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _watchTask;

    // 重试配置
    private const int MaxRetries = 5;
    private const int InitialDelayMs = 2000;
    private const int MaxDelayMs = 60000;
    private const double BackoffMultiplier = 2.0;

    public DriverWatchdog(
        string instanceId,
        IProtocolDriver driver,
        GatewayRuntime runtime,
        ILogger logger)
    {
        _instanceId = instanceId;
        _driver = driver;
        _runtime = runtime;
        _logger = logger;

        _watchTask = Task.Run(WatchLoopAsync);
    }

    private async Task WatchLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // 每 5 秒检查一次驱动状态
                await Task.Delay(5000, _cts.Token);

                var info = _runtime.GetDriverInfo(_instanceId);
                if (info == null)
                {
                    // 驱动已被移除
                    return;
                }

                // 检测驱动是否处于 Error 状态（由异常事件触发）
                if (info.Status == RuntimeStatus.Error)
                {
                    _logger.LogWarning("[Watchdog] 驱动 {InstanceId} 处于 Error 状态，开始自动恢复...",
                        _instanceId);

                    await TryRecoverAsync();
                    return; // 恢复流程结束（无论成功失败），看门狗退出
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Watchdog] 驱动 {InstanceId} 看门狗异常退出", _instanceId);
        }
    }

    private async Task TryRecoverAsync()
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            if (_cts.IsCancellationRequested) return;

            var delayMs = Math.Min(
                (int)(InitialDelayMs * Math.Pow(BackoffMultiplier, attempt - 1)),
                MaxDelayMs);

            _logger.LogInformation(
                "[Watchdog] 驱动 {InstanceId} 第 {Attempt}/{Max} 次恢复尝试，等待 {Delay}ms...",
                _instanceId, attempt, MaxRetries, delayMs);

            await Task.Delay(delayMs, _cts.Token);

            try
            {
                await _runtime.RestartDriverAsync(_instanceId, _cts.Token);

                var info = _runtime.GetDriverInfo(_instanceId);
                if (info?.Status == RuntimeStatus.Running)
                {
                    _logger.LogInformation("[Watchdog] 驱动 {InstanceId} 恢复成功（第 {Attempt} 次）",
                        _instanceId, attempt);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Watchdog] 驱动 {InstanceId} 第 {Attempt} 次恢复失败", _instanceId, attempt);
            }
        }

        // 超过最大重试次数，标记为 Error（需手动恢复）
        var errorInfo = _runtime.GetDriverInfo(_instanceId);
        if (errorInfo != null)
        {
            errorInfo.Status = RuntimeStatus.Error;
            errorInfo.ErrorMessage = $"看门狗恢复失败（已重试 {MaxRetries} 次），需手动恢复";
        }

        _logger.LogError(
            "[Watchdog] 驱动 {InstanceId} 恢复失败，已达到最大重试次数 {Max}，标记为 Error 等待手动恢复",
            _instanceId, MaxRetries);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _watchTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* 忽略 */ }
        _cts.Dispose();
    }
}
