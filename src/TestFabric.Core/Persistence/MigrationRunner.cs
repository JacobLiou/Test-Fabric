using System.Reflection;
using Dapper;
using Npgsql;

namespace TestFabric.Core.Persistence;

/// <summary>
/// 嵌入式 SQL 迁移运行器：按文件名序号升序执行，逐个事务提交，幂等（schema_migrations 记录已应用版本）。
/// 启动时调用；Fail-Closed——迁移失败则终止进程，避免带伤运行。
/// </summary>
public static class MigrationRunner
{
    public static async Task ApplyAsync(NpgsqlConnection conn, CancellationToken ct = default)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "CREATE TABLE IF NOT EXISTS schema_migrations (version INT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())",
            cancellationToken: ct));

        var applied = (await conn.QueryAsync<int>(new CommandDefinition(
            "SELECT version FROM schema_migrations", cancellationToken: ct))).ToHashSet();

        foreach (var (version, sql) in LoadEmbeddedMigrations().OrderBy(m => m.Version))
        {
            if (applied.Contains(version)) continue;
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(sql, transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO schema_migrations(version) VALUES (@version)",
                    new { version }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
    }

    private static IEnumerable<(int Version, string Sql)> LoadEmbeddedMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = name.Split('.', StringSplitOptions.RemoveEmptyEntries)[^2] + ".sql"; // 001_core.sql
            var version = int.Parse(fileName.Split('_')[0]);
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (version, reader.ReadToEnd());
        }
    }
}
