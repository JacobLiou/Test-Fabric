using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestFabric.Core.Options;
using TestFabric.Core.Services;

namespace TestFabric.Core.Background;

/// <summary>
/// 租约过期扫描器：周期性地把心跳超时的 Active Lease 置为 Expired 并执行 Recovery（FR-05）。
/// 与 Release/ForceRelease 通过 leases.status='Active' 的条件更新天然互斥。
/// </summary>
public sealed class LeaseExpiryScanner(
    IServiceScopeFactory scopes,
    ILogger<LeaseExpiryScanner> logger,
    Microsoft.Extensions.Options.IOptions<TestFabricOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Scanner.Enabled)
        {
            logger.LogInformation("Lease expiry scanner disabled by configuration");
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(100, options.Value.Scanner.IntervalMs));
        logger.LogInformation("Lease expiry scanner started, interval {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var leases = scope.ServiceProvider.GetRequiredService<LeaseService>();
                var recovered = await leases.RecoverExpiredLeasesAsync(stoppingToken);
                if (recovered > 0)
                    logger.LogInformation("Expired and recovered {Count} lease(s)", recovered);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // 扫描器是系统不变量的守护者，失败不能静默，也不能让进程退出
                logger.LogError(ex, "Lease expiry scan failed; retrying next interval");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        logger.LogInformation("Lease expiry scanner stopped");
    }
}
