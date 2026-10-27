using System.Globalization;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using TestFabric.Contracts;
using TestFabric.Core.Domain;
using TestFabric.Core.Matching;
using TestFabric.Core.Options;
using TestFabric.Core.Persistence;
using TestFabric.Core.Ports;

namespace TestFabric.Core.Services;

/// <summary>资源注册与配置管理（FR-01）。</summary>
public sealed class RegistryService(
    IDbConnectionFactory db,
    CapabilityService capabilities,
    AuditService audit,
    IAdapterCatalog adapterCatalog,
    IInstrumentGateway gateway,
    TimeProvider time,
    IOptions<TestFabricOptions> options)
{
    private static readonly string[] AllowedAddressTypes = ["TCPIP", "GPIB", "SERIAL", "SIMULATOR"];

    public async Task<ResourceDto> CreateAsync(CreateResourceRequest req, string actor, CancellationToken ct)
    {
        ValidateRequired(req);
        var calibrationDue = ParseCalibrationDate(req.CalibrationDueDate);

        // 校验能力声明（属性名拼写、类型）
        var validated = new List<(string CapabilityId, string PropertiesJson)>();
        foreach (var assignment in req.Capabilities ?? [])
        {
            var schema = await capabilities.GetRequiredSchemaAsync(assignment.CapabilityId, ct);
            CapabilityService.ValidateProperties(schema, assignment.Properties);
            validated.Add((assignment.CapabilityId, System.Text.Json.JsonSerializer.Serialize(assignment.Properties)));
        }

        await using var conn = await db.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO resources (resource_id, resource_type, manufacturer, model, serial_number, asset_tag,
                    address_type, address, adapter_id, adapter_version, location, description, calibration_due_date)
                VALUES (@ResourceId, @ResourceType, @Manufacturer, @Model, @SerialNumber, @AssetTag,
                    @AddressType, @Address, @AdapterId, @AdapterVersion, @Location, @Description, @CalibrationDueDate)
                """,
                new
                {
                    req.ResourceId,
                    req.ResourceType,
                    req.Manufacturer,
                    req.Model,
                    SerialNumber = req.SerialNumber ?? "",
                    AssetTag = req.AssetTag ?? "",
                    req.AddressType,
                    req.Address,
                    req.AdapterId,
                    AdapterVersion = adapterCatalog.GetVersion(req.AdapterId),
                    req.Location,
                    Description = req.Description ?? "",
                    CalibrationDueDate = calibrationDue,
                }, tx, cancellationToken: ct));

            foreach (var (capabilityId, propertiesJson) in validated)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO resource_capabilities (resource_id, capability_id, properties_json) VALUES (@R, @C, @P::jsonb)",
                    new { R = req.ResourceId, C = capabilityId, P = propertiesJson }, tx, cancellationToken: ct));
            }

            var evt = await audit.AppendAsync(new EventDraft(EventTypes.ResourceCreated, req.ResourceId, Actor: actor,
                Payload: new()
                {
                    ["resourceType"] = req.ResourceType,
                    ["manufacturer"] = req.Manufacturer,
                    ["model"] = req.Model,
                    ["adapterId"] = req.AdapterId,
                    ["addressType"] = req.AddressType,
                }), conn, tx, ct);
            await tx.CommitAsync(ct);
            audit.Publish(evt);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            throw new FabricException(ErrorCodes.ResourceIdDuplicated, $"resource '{req.ResourceId}' already exists (AC-01-02)");
        }

        return (await GetDtoAsync(req.ResourceId, ct))!;
    }

    public async Task<ResourceDto?> GetDtoAsync(string resourceId, CancellationToken ct)
    {
        var resource = await GetAsync(resourceId, ct);
        if (resource is null) return null;
        var all = await ToDtosAsync([resource], ct);
        return all.SingleOrDefault();
    }

    public async Task<Resource?> GetAsync(string resourceId, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Resource>(new CommandDefinition(
            "SELECT * FROM resources WHERE resource_id = @Id AND deleted_at IS NULL", new { Id = resourceId }, cancellationToken: ct));
        return row;
    }

    /// <summary>加载资源池（Discovery 与 Acquire 共用）：未删除资源 + 指定能力的声明属性。</summary>
    public async Task<IReadOnlyList<Resource>> LoadPoolAsync(string capabilityId, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Resource, string?, Resource>(new CommandDefinition(
            """
            SELECT r.*, rc.properties_json
            FROM resources r
            LEFT JOIN resource_capabilities rc ON rc.resource_id = r.resource_id AND rc.capability_id = @Cap
            WHERE r.deleted_at IS NULL
            """,
            new { Cap = capabilityId }, cancellationToken: ct),
            (resource, propsJson) =>
            {
                resource.CapabilityProperties = propsJson is null ? null : JsonPlain.ToDictionary(propsJson);
                return resource;
            },
            splitOn: "properties_json");
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Resource>> ListForHealthCheckAsync(CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Resource>(new CommandDefinition(
            "SELECT * FROM resources WHERE deleted_at IS NULL AND administrative_state = 'Enabled'", cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<PagedResult<ResourceDto>> ListAsync(
        string? status, string? type, string? location, int offset, int limit, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var where = "WHERE deleted_at IS NULL";
        var param = new DynamicParameters();
        param.Add("Limit", limit);
        param.Add("Offset", offset);
        if (!string.IsNullOrEmpty(status)) { where += " AND operational_state = @Status"; param.Add("Status", status); }
        if (!string.IsNullOrEmpty(type)) { where += " AND resource_type = @Type"; param.Add("Type", type); }
        if (!string.IsNullOrEmpty(location)) { where += " AND location = @Location"; param.Add("Location", location); }

        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT count(*) FROM resources {where}", param, cancellationToken: ct));
        var rows = (await conn.QueryAsync<Resource>(new CommandDefinition(
            $"SELECT * FROM resources {where} ORDER BY resource_id LIMIT @Limit OFFSET @Offset", param, cancellationToken: ct))).ToList();
        return new PagedResult<ResourceDto>(await ToDtosAsync(rows, ct), total, offset, limit);
    }

    /// <summary>批量映射为 DTO，附带能力声明与当前占用（FR-09-02）。</summary>
    public async Task<IReadOnlyList<ResourceDto>> ToDtosAsync(IReadOnlyList<Resource> resources, CancellationToken ct)
    {
        if (resources.Count == 0) return [];
        var ids = resources.Select(r => r.ResourceId).ToArray();
        await using var conn = await db.CreateOpenConnectionAsync(ct);

        var caps = (await conn.QueryAsync<(string ResourceId, string CapabilityId, string Properties)>(new CommandDefinition(
            "SELECT resource_id, capability_id, properties_json FROM resource_capabilities WHERE resource_id = ANY(@Ids)",
            new { Ids = ids }, cancellationToken: ct)))
            .GroupBy(x => x.ResourceId)
            .ToDictionary(g => g.Key, g => g.Select(x => new ResourceCapabilityDto(x.CapabilityId, JsonPlain.ToDictionary(x.Properties))).ToList());

        var leases = (await conn.QueryAsync<Lease>(new CommandDefinition(
            "SELECT * FROM leases WHERE status = 'Active' AND resource_id = ANY(@Ids)", new { Ids = ids }, cancellationToken: ct)))
            .ToDictionary(l => l.ResourceId);

        return resources.Select(r => new ResourceDto(
            r.ResourceId, r.ResourceType, r.Manufacturer, r.Model, r.AddressType, r.Address, r.AdapterId,
            r.AdapterVersion, r.Location,
            NullIfEmpty(r.SerialNumber), NullIfEmpty(r.AssetTag), NullIfEmpty(r.Description),
            r.AdministrativeState, r.OperationalState, r.HealthState, r.ConnectivityState,
            r.CalibrationState(time), r.CalibrationDueDate, r.CreatedAt, r.UpdatedAt,
            leases.TryGetValue(r.ResourceId, out var lease) ? ToLeaseSummary(lease) : null,
            caps.TryGetValue(r.ResourceId, out var list) ? list : [])).ToList();
    }

    public async Task<ResourceDto> UpdateAsync(string resourceId, UpdateResourceRequest req, string actor, CancellationToken ct)
    {
        var existing = await GetAsync(resourceId, ct)
            ?? throw new FabricException(ErrorCodes.ResourceNotFound, $"resource '{resourceId}' not found");

        var sets = new List<string>();
        var p = new DynamicParameters();
        p.Add("ResourceId", resourceId);
        var changes = new Dictionary<string, string>(); // AC-01-06：审计记录旧值与新值

        void Field(string column, string? raw, string old, Action<string>? validate = null)
        {
            if (raw is null) return;
            validate?.Invoke(raw);
            if (raw == old) return;
            sets.Add($"{column} = @{column}");
            p.Add(column, raw);
            changes[column] = $"{old} -> {raw}";
        }

        Field("manufacturer", req.Manufacturer, existing.Manufacturer);
        Field("model", req.Model, existing.Model);
        Field("location", req.Location, existing.Location);
        Field("serial_number", req.SerialNumber, existing.SerialNumber);
        Field("asset_tag", req.AssetTag, existing.AssetTag);
        Field("description", req.Description, existing.Description);
        Field("address_type", req.AddressType, existing.AddressType, v =>
        {
            if (!AllowedAddressTypes.Contains(v, StringComparer.OrdinalIgnoreCase))
                throw FabricException.Validation($"addressType must be one of: {string.Join(", ", AllowedAddressTypes)}");
        });
        Field("address", req.Address, existing.Address, v =>
        {
            var newType = req.AddressType ?? existing.AddressType;
            if (!newType.Equals("SIMULATOR", StringComparison.OrdinalIgnoreCase)) ValidateTcpAddress(v);
        });

        if (req.CalibrationDueDate is not null)
        {
            var due = ParseCalibrationDate(req.CalibrationDueDate);
            if (due != existing.CalibrationDueDate)
            {
                sets.Add("calibration_due_date = @calibration_due_date");
                p.Add("calibration_due_date", due);
                changes["calibration_due_date"] = $"{existing.CalibrationDueDate} -> {due}";
            }
        }

        if (sets.Count > 0)
        {
            await using var conn = await db.CreateOpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                $"UPDATE resources SET {string.Join(", ", sets)}, updated_at = now() WHERE resource_id = @ResourceId", p, cancellationToken: ct));
            await audit.EmitAsync(new EventDraft(EventTypes.ResourceUpdated, resourceId, Actor: actor,
                Payload: changes.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)), ct);
        }
        return (await GetDtoAsync(resourceId, ct))!;
    }

    public async Task<ResourceDto> SetAdministrativeStateAsync(string resourceId, string target, string actor, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var busy = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM leases WHERE resource_id = @Id AND status = 'Active'", new { Id = resourceId }, tx, cancellationToken: ct));

        // AC-12-01：Busy 资源禁止直接禁用/转维护，必须先 Release 或强制释放
        if (busy > 0 && target is States.Administrative.Disabled or States.Administrative.Maintenance)
            throw new FabricException(ErrorCodes.ResourceBusy,
                $"resource '{resourceId}' is Busy; release its lease before setting {target} (AC-12-01)");

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE resources SET administrative_state = @Target, updated_at = now() WHERE resource_id = @Id AND deleted_at IS NULL",
            new { Id = resourceId, Target = target }, tx, cancellationToken: ct));
        if (updated == 0)
            throw new FabricException(ErrorCodes.ResourceNotFound, $"resource '{resourceId}' not found");

        var evt = await audit.AppendAsync(new EventDraft(
            target == States.Administrative.Maintenance ? EventTypes.MaintenanceSet :
            target == States.Administrative.Disabled ? EventTypes.ResourceDisabled : EventTypes.ResourceEnabled,
            resourceId, Actor: actor, Payload: new() { ["administrativeState"] = target }), conn, tx, ct);
        await tx.CommitAsync(ct);
        audit.Publish(evt);
        return (await GetDtoAsync(resourceId, ct))!;
    }

    public async Task DeleteAsync(string resourceId, string actor, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE resources SET deleted_at = now(), administrative_state = 'Disabled', updated_at = now() WHERE resource_id = @Id AND deleted_at IS NULL",
            new { Id = resourceId }, cancellationToken: ct));
        if (updated == 0) throw new FabricException(ErrorCodes.ResourceNotFound, $"resource '{resourceId}' not found");
        await audit.EmitAsync(new EventDraft(EventTypes.ResourceDeleted, resourceId, Actor: actor,
            Payload: new() { ["logicalDelete"] = true }), ct);  // AC-01-05：保留历史记录
    }

    /// <summary>管理员对 Error 资源执行复位确认（AC-12-02）：Error → Recovering →(Reset)→ Available/Error。</summary>
    public async Task<ResourceDto> ResetFromErrorAsync(string resourceId, string actor, CancellationToken ct)
    {
        var resource = await GetAsync(resourceId, ct)
            ?? throw new FabricException(ErrorCodes.ResourceNotFound, $"resource '{resourceId}' not found");
        if (resource.OperationalState != States.Operational.Error)
            throw new FabricException(ErrorCodes.StateConflict,
                $"resource '{resourceId}' is {resource.OperationalState}; reset-from-error requires Error state");

        await ExecuteUpdateAsync("UPDATE resources SET operational_state = 'Recovering', updated_at = now() WHERE resource_id = @Id AND operational_state = 'Error'",
            new { Id = resourceId }, ct);
        var outcome = await gateway.ResetAsync(resourceId, ct);
        var target = outcome == GatewayOutcome.Succeeded ? States.Operational.Available : States.Operational.Error;
        await ExecuteUpdateAsync(
            """
            UPDATE resources
            SET operational_state = @Target,
                last_released_at = CASE WHEN @Target = 'Available' THEN now() ELSE last_released_at END,
                updated_at = now()
            WHERE resource_id = @Id AND operational_state = 'Recovering'
            """,
            new { Target = target, Id = resourceId }, ct);
        await audit.EmitAsync(new EventDraft(EventTypes.ResourceStateChanged, resourceId, Actor: actor,
            Payload: new() { ["from"] = States.Operational.Error, ["to"] = target, ["cause"] = "admin-reset-from-error" }), ct);
        return (await GetDtoAsync(resourceId, ct))!;
    }

    private async Task ExecuteUpdateAsync(string sql, object param, CancellationToken ct)
    {
        await using var conn = await db.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, param, cancellationToken: ct));
    }

    private static void ValidateRequired(CreateResourceRequest req)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(req.ResourceId)) missing.Add("resourceId");
        if (string.IsNullOrWhiteSpace(req.ResourceType)) missing.Add("resourceType");
        if (string.IsNullOrWhiteSpace(req.Manufacturer)) missing.Add("manufacturer");
        if (string.IsNullOrWhiteSpace(req.Model)) missing.Add("model");
        if (string.IsNullOrWhiteSpace(req.AddressType)) missing.Add("addressType");
        if (string.IsNullOrWhiteSpace(req.Address)) missing.Add("address");
        if (string.IsNullOrWhiteSpace(req.AdapterId)) missing.Add("adapterId");
        if (string.IsNullOrWhiteSpace(req.Location)) missing.Add("location");
        if (missing.Count > 0)
            throw FabricException.Validation($"missing required fields: {string.Join(", ", missing)}", new() { ["fields"] = missing });

        if (!AllowedAddressTypes.Contains(req.AddressType, StringComparer.OrdinalIgnoreCase))
            throw FabricException.Validation($"addressType must be one of: {string.Join(", ", AllowedAddressTypes)}");
        if (!req.AddressType.Equals("SIMULATOR", StringComparison.OrdinalIgnoreCase))
            ValidateTcpAddress(req.Address);
        if (req.ResourceId.Length > 64) throw FabricException.Validation("resourceId must be <= 64 chars");
    }

    private static void ValidateTcpAddress(string address)
    {
        // host 或 host:port
        var host = address;
        var port = 0;
        var idx = address.LastIndexOf(':');
        if (idx >= 0)
        {
            host = address[..idx];
            if (!int.TryParse(address[(idx + 1)..], out port) || port is < 1 or > 65535)
                throw FabricException.Validation($"address '{address}' has an invalid port");
        }
        if (string.IsNullOrWhiteSpace(host))
            throw FabricException.Validation($"address '{address}' has an empty host");
    }

    /// <summary>校准日期语义（评审 P1）：date-only 输入按「当日 23:59:59 UTC」解释，即有效期覆盖整个到期日。</summary>
    public static DateTimeOffset? ParseCalibrationDate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        if (DateTimeOffset.TryParseExact(input, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dateOnly))
            return dateOnly.AddDays(1).AddSeconds(-1);
        if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var full))
            return full;
        throw FabricException.Validation($"calibrationDueDate '{input}' is not a valid date (expected yyyy-MM-dd or ISO 8601)");
    }

    private static LeaseSummaryDto ToLeaseSummary(Lease lease) => new(
        lease.LeaseId, lease.ClientId, NullIfEmpty(lease.WorkOrder), NullIfEmpty(lease.SerialNumber),
        NullIfEmpty(lease.Operation), lease.ExpiresAt, lease.TtlSeconds);

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
