using System.Text.Json;
using Dapper;
using Npgsql;
using TestFabric.Contracts;
using TestFabric.Core.Domain;
using TestFabric.Core.Persistence;

namespace TestFabric.Core.Services;

/// <summary>
/// 能力注册与 schema 管理（FR-02）。
/// Schema 用于：注册期校验资源声明的属性（防拼写错误导致 Discovery 静默不匹配）、
/// 请求期校验约束字段（AC-02-04 UNSUPPORTED_CONSTRAINT）。
/// </summary>
public sealed class CapabilityService(IDbConnectionFactory db, AuditService audit, TimeProvider time)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CapabilityDto> CreateAsync(CreateCapabilityRequest req, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.CapabilityId))
            throw FabricException.Validation("capabilityId is required");
        if (req.Version < 1) throw FabricException.Validation("version must be >= 1");
        if (req.Schema.Properties is null || req.Schema.Properties.Count == 0)
            throw FabricException.Validation("schema must define at least one property");

        foreach (var (name, def) in req.Schema.Properties)
        {
            var type = def.Type;
            if (type is not (CapabilityPropertyDef.Number or CapabilityPropertyDef.Boolean
                or CapabilityPropertyDef.String or CapabilityPropertyDef.Array))
            {
                throw FabricException.Validation(
                    $"property '{name}' has invalid type '{type}' (allowed: number, boolean, string, array)");
            }
        }

        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var schemaJson = JsonSerializer.Serialize(new { properties = req.Schema.Properties }, JsonOptions);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO capabilities (capability_id, version, schema_json) VALUES (@Id, @Version, @Schema::jsonb)",
                new { Id = req.CapabilityId, req.Version, Schema = schemaJson }, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new FabricException(ErrorCodes.ResourceIdDuplicated, $"capability '{req.CapabilityId}' already exists");
        }

        var evt = await audit.AppendAsync(new EventDraft(EventTypes.CapabilityCreated, Actor: actor,
            Payload: new() { ["capabilityId"] = req.CapabilityId, ["version"] = req.Version }), conn, ct: ct);
        audit.Publish(evt);
        return new CapabilityDto(req.CapabilityId, req.Version, req.Schema);
    }

    public async Task<IReadOnlyList<CapabilityDto>> ListAsync(CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string Id, int Version, string Schema)>(new CommandDefinition(
            "SELECT capability_id, version, schema_json FROM capabilities ORDER BY capability_id", cancellationToken: ct));
        return rows.Select(r => new CapabilityDto(r.Id, r.Version,
            JsonSerializer.Deserialize<CapabilitySchema>(r.Schema, JsonOptions) ?? new([]))).ToList();
    }

    public async Task<CapabilitySchema> GetRequiredSchemaAsync(string capabilityId, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var schemaJson = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT schema_json FROM capabilities WHERE capability_id = @Id", new { Id = capabilityId }, cancellationToken: ct));
        if (schemaJson is null)
            throw new FabricException(ErrorCodes.CapabilityNotFound, $"capability '{capabilityId}' is not registered");
        return JsonSerializer.Deserialize<CapabilitySchema>(schemaJson, JsonOptions)
            ?? throw new FabricException(ErrorCodes.CapabilityNotFound, $"capability '{capabilityId}' has an invalid schema");
    }

    /// <summary>注册期校验：资源声明的能力属性必须存在于 schema 且类型匹配（防止拼写错误）。</summary>
    public static void ValidateProperties(CapabilitySchema schema, Dictionary<string, object?> properties)
    {
        foreach (var (name, value) in properties)
        {
            if (!schema.Properties.TryGetValue(name, out var def))
                throw FabricException.Validation(
                    $"capability property '{name}' is not defined in the schema (defined: {string.Join(", ", schema.Properties.Keys)})");
            if (!TypeMatches(def.Type, value))
                throw FabricException.Validation($"capability property '{name}' must be of type '{def.Type}'");
        }
    }

    private static bool TypeMatches(string expectedType, object? value) => expectedType switch
    {
        CapabilityPropertyDef.Number => value is double or int or long or float,
        CapabilityPropertyDef.Boolean => value is bool,
        CapabilityPropertyDef.String => value is string,
        CapabilityPropertyDef.Array => value is List<object?>,
        _ => false,
    };
}
