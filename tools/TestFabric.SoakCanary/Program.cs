using System.Net.Http.Json;
using System.Text.Json;
using TestFabric.Sdk;

// Test Fabric 不变量金丝雀（评审 P3 / E2E-06）：
// 多工位并发 Acquire→Execute→Release，随机注入客户端异常退出（放弃心跳），
// 周期性断言三大不变量：无双重分配、无不可解释的僵尸租约、资源可恢复。
//
// 用法：
//   TestFabric.SoakCanary --url http://localhost:5080 --token dev-client-token --admin-token dev-admin-token \
//        --capability OpticalSpectrumAnalysis --stations 3 --duration 00:05:00 [--chaos]

var url = Arg(args, "--url", "http://localhost:5080");
var token = Arg(args, "--token", "dev-client-token");
var adminToken = Arg(args, "--admin-token", "dev-admin-token");
var capability = Arg(args, "--capability", "OpticalSpectrumAnalysis");
var stations = int.Parse(Arg(args, "--stations", "3"));
var duration = TimeSpan.Parse(Arg(args, "--duration", "00:05:00"));
var chaos = args.Contains("--chaos");

Console.WriteLine($"TestFabric Soak Canary");
Console.WriteLine($"  url={url} capability={capability} stations={stations} duration={duration} chaos={chaos}");
Console.WriteLine();

var stats = new Stats();
using var cts = new CancellationTokenSource(duration);
var tasks = Enumerable.Range(1, stations)
    .Select(i => StationLoopAsync(i, cts.Token))
    .Append(CanaryLoopAsync(cts.Token))
    .ToList();

try { await Task.WhenAll(tasks); }
catch (OperationCanceledException) { }

Console.WriteLine();
Console.WriteLine("==== 结果 ====");
Console.WriteLine($"Acquire 成功/失败: {stats.Acquired}/{stats.AcquireFailed}");
Console.WriteLine($"释放(正常/放弃): {stats.Released}/{stats.Abandoned}");
Console.WriteLine($"过期回收: {stats.ExpiredRecovered}");
Console.WriteLine($"测量: {stats.Measurements}, 测量失败: {stats.MeasureFailed}");
Console.WriteLine($"不变量违规: 双重分配={stats.DoubleAllocations}, 僵尸租约={stats.Zombies}");
Console.WriteLine(stats.DoubleAllocations == 0 && stats.Zombies == 0
    ? "PASS：全部不变量成立"
    : "FAIL：存在不变量违规，禁止进入下一阶段（Go/No-Go §16）");
return stats.DoubleAllocations == 0 && stats.Zombies == 0 ? 0 : 1;

// ----------------------------------------------------------------

async Task StationLoopAsync(int station, CancellationToken ct)
{
    var rng = new Random(station * 7919);
    var clientId = $"CANARY-ST{station:D2}";
    while (!ct.IsCancellationRequested)
    {
        using var client = new TestFabricClient(new Uri(url), token, clientId);
        try
        {
            await using var lease = await client.AcquireAsync(new(
                clientId,
                capability)
            {
                Context = new(WorkOrder: $"WO-CANARY-{rng.Next(9999):D4}", SerialNumber: $"SN-{rng.Next(99999):D5}", Operation: "SoakTest"),
                LeaseTtlSeconds = 20,
            }, idempotencyKey: $"{clientId}-{stats.Acquired + stats.Abandoned}", ct);

            lease.LeaseLost += (_, code) =>
            {
                Console.WriteLine($"[{clientId}] LEASE LOST ({code}) — 业务必须中止且不得写结果");
                Interlocked.Increment(ref stats.ExpiredRecovered);
            };
            Interlocked.Increment(ref stats.Acquired);

            if (capability.Contains("OSA", StringComparison.OrdinalIgnoreCase))
            {
                var sweep = await client.OsaSingleSweepAsync(lease.LeaseId, ct);
                var trace = await client.OsaGetTraceAsync(lease.LeaseId, sweep.SweepId, ct);
                if (trace.ValuesDbm.Length == 0) throw new InvalidOperationException("empty trace");
            }
            else
            {
                var power = await client.PmMeasurePowerAsync(lease.LeaseId, 1, wavelengthNm: 1550, ct: ct);
                _ = power.PowerDbm;
            }
            Interlocked.Increment(ref stats.Measurements);

            if (chaos && rng.Next(100) < 15)
            {
                // 模拟客户端崩溃：放弃心跳与释放（不调用 Dispose 的 Release —— 用裸 client 直接断开）
                Interlocked.Increment(ref stats.Abandoned);
                break; // 销毁 client（心跳停止）→ TTL 后由过期扫描回收
            }

            await lease.ReleaseAsync("Completed", ct);
            Interlocked.Increment(ref stats.Released);
            await Task.Delay(RandomDelay(rng), ct);
        }
        catch (FabricSdkException ex)
        {
            if (ex.Code == "NO_RESOURCE_AVAILABLE") { Interlocked.Increment(ref stats.AcquireFailed); await Task.Delay(500, ct); }
            else { Interlocked.Increment(ref stats.MeasureFailed); Console.WriteLine($"[{clientId}] {ex.Code}: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref stats.MeasureFailed);
            Console.WriteLine($"[{clientId}] unexpected: {ex.Message}");
        }
    }
}

async Task CanaryLoopAsync(CancellationToken ct)
{
    // 每 5 秒检查不变量（评审 P3：72h Soak 需要"证明不变量"而不是"跑了 72h"）
    using var http = new HttpClient { BaseAddress = new Uri(url) };
    http.DefaultRequestHeaders.Add("X-Api-Token", adminToken);

    while (!ct.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            using var response = await http.GetAsync("api/v1/leases?status=Active&limit=500", ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            // 不变量 1：无双重分配
            var active = body.GetProperty("items").EnumerateArray().ToList();
            var doubles = active.GroupBy(l => l.GetProperty("resourceId").GetString())
                .Count(g => g.Count() > 1);
            if (doubles > 0)
            {
                Interlocked.Add(ref stats.DoubleAllocations, doubles);
                Console.WriteLine($"!! 不变量违规：{doubles} 个资源存在多个 Active Lease");
            }

            // 不变量 2：无不可解释的僵尸租约（Active 超过 TTL+宽限仍未回收）
            var serverNow = DateTime.UtcNow;
            var zombies = active.Count(l =>
            {
                var expires = l.GetProperty("expiresAt").GetDateTimeOffset();
                return expires < serverNow.AddSeconds(-15); // TTL≤20s + 扫描 1s + 宽限
            });
            if (zombies > 0)
            {
                Interlocked.Add(ref stats.Zombies, zombies);
                Console.WriteLine($"!! 不变量违规：{zombies} 个僵尸租约（Active 但已过期未回收）");
            }

            Console.WriteLine($"[canary] active={active.Count} acquired={stats.Acquired} released={stats.Released} abandoned={stats.Abandoned} zombies={stats.Zombies}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[canary] check failed: {ex.Message}");
        }
    }
}

static TimeSpan RandomDelay(Random rng)
    => TimeSpan.FromMilliseconds(rng.Next(1000, 5000));

static string Arg(string[] args, string name, string fallback)
    => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback;

internal sealed class Stats
{
    public int Acquired;
    public int AcquireFailed;
    public int Released;
    public int Abandoned;
    public int ExpiredRecovered;
    public int Measurements;
    public int MeasureFailed;
    public int DoubleAllocations;
    public int Zombies;
}
