using Npgsql;

namespace TestFabric.Core.Persistence;

public interface IDbConnectionFactory
{
    Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default);
}

public sealed class NpgsqlConnectionFactory : IDbConnectionFactory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlConnectionFactory(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
    }

    public async Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = _dataSource.CreateConnection();
        await conn.OpenAsync(ct);
        return conn;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
