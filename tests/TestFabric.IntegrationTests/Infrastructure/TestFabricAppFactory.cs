using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TestFabric.IntegrationTests.Infrastructure;

/// <summary>进程内启动完整 API（含后台服务、迁移、启动宽限），并注入测试专用配置。</summary>
public sealed class TestFabricAppFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:testfabric"] = connectionString,
                // 测试节奏：健康检查 1s、扫描 300ms、阈值降低
                ["TestFabric:Health:IntervalSeconds"] = "1",
                ["TestFabric:Health:FailThreshold"] = "2",
                ["TestFabric:Health:SuccessThreshold"] = "1",
                ["TestFabric:Health:ProbeWaitMs"] = "1000",
                ["TestFabric:Scanner:IntervalMs"] = "300",
                ["TestFabric:Lease:MinTtlSeconds"] = "2",
                // 确定性测试令牌
                ["Auth:Tokens:0:Token"] = "test-admin-token",
                ["Auth:Tokens:0:Role"] = "Administrator",
                ["Auth:Tokens:0:Subject"] = "test-admin",
                ["Auth:Tokens:1:Token"] = "test-engineer-token",
                ["Auth:Tokens:1:Role"] = "EquipmentEngineer",
                ["Auth:Tokens:1:Subject"] = "test-engineer",
                ["Auth:Tokens:2:Token"] = "test-client-token",
                ["Auth:Tokens:2:Role"] = "TestClient",
                ["Auth:Tokens:2:Subject"] = "test-client",
                ["Auth:Tokens:3:Token"] = "test-viewer-token",
                ["Auth:Tokens:3:Role"] = "Viewer",
                ["Auth:Tokens:3:Subject"] = "test-viewer",
            });
        });
    }
}
