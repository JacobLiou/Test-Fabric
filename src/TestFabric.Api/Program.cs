using Serilog;
using TestFabric.Adapters;
using TestFabric.Adapters.Simulator;
using TestFabric.Api.Endpoints;
using TestFabric.Api.Infrastructure;
using TestFabric.Core.Background;
using TestFabric.Core.Events;
using TestFabric.Core.Options;
using TestFabric.Core.Persistence;
using TestFabric.Core.Ports;
using TestFabric.Core.Services;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Test Fabric API starting");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "TestFabric.Api"));

    // ---- 模块化单体：单进程、单库（评审 P2） ----
    builder.Services.AddOptions<TestFabricOptions>()
        .Bind(builder.Configuration.GetSection(TestFabricOptions.SectionName))
        .ValidateDataAnnotations();

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<EventBus>();
    builder.Services.AddSingleton<IDbConnectionFactory>(sp =>
        new NpgsqlConnectionFactory(sp.GetRequiredService<IConfiguration>().GetConnectionString("testfabric")!));
    builder.Services.AddScoped<AuditService>();
    builder.Services.AddScoped<CapabilityService>();
    builder.Services.AddScoped<RegistryService>();
    builder.Services.AddScoped<DiscoveryService>();
    builder.Services.AddScoped<LeaseService>();
    builder.Services.AddScoped<ExecuteEndpoints.Exec>();

    // ---- Adapters ----
    builder.Services.AddSingleton<SimulatorFaults>();
    builder.Services.AddSingleton<AdapterRegistry>(sp =>
    {
        var registry = new AdapterRegistry();
        var faults = sp.GetRequiredService<SimulatorFaults>();
        registry.Register(new SimulatorOsaAdapter(faults));
        registry.Register(new SimulatorPowerMeterAdapter(faults));
        return registry;
    });
    builder.Services.AddSingleton<IAdapterCatalog>(sp => sp.GetRequiredService<AdapterRegistry>());
    builder.Services.AddSingleton<IInstrumentGateway, InstrumentGateway>();

    // ---- 后台服务：租约过期扫描 + 健康监控 ----
    builder.Services.AddHostedService<LeaseExpiryScanner>();
    builder.Services.AddHostedService<HealthMonitorService>();

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
    {
        o.SwaggerDoc("v1", new() { Title = "Test Fabric API", Version = "v1" });
        o.AddSecurityDefinition("bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "API Token（V0.1 静态令牌，见 appsettings.json Auth:Tokens）",
        });
    });

    var app = builder.Build();

    // ---- 启动：迁移 + 租约宽限（Fail-Closed，NFR-02）----
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var opts = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TestFabricOptions>>().Value;
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        await using var conn = await db.CreateOpenConnectionAsync();
        await MigrationRunner.ApplyAsync(conn);
        await StartupPolicy.ApplyLeaseGraceAsync(db, audit, logger, opts.Startup.LeaseGraceEnabled, default);
    }

    app.UseMiddleware<ErrorHandlingMiddleware>();
    app.UseMiddleware<TokenAuthMiddleware>();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseSwagger();
    app.UseSwaggerUI();

    app.MapResourceEndpoints();
    app.MapCapabilityEndpoints();
    app.MapDiscoveryEndpoints();
    app.MapLeaseEndpoints();
    app.MapExecuteEndpoints();
    app.MapEventEndpoints();
    app.MapSimulatorEndpoints();
    app.MapGet("/healthz", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Test Fabric API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }
