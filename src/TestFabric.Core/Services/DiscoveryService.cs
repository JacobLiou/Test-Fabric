using TestFabric.Contracts;
using TestFabric.Core.Matching;

namespace TestFabric.Core.Services;

/// <summary>资源发现（FR-03）：只读查询，不产生任何状态变化（AC-03-05）。</summary>
public sealed class DiscoveryService(
    RegistryService registry,
    CapabilityService capabilities,
    TimeProvider time)
{
    public async Task<DiscoverResponse> DiscoverAsync(DiscoverRequest req, CancellationToken ct)
    {
        var schema = await capabilities.GetRequiredSchemaAsync(req.Capability, ct);
        var constraints = ConstraintParser.Parse(req.Constraints);
        ConstraintParser.EnsureDefined(schema, constraints); // AC-02-04：未知约束不得静默忽略

        var pool = await registry.LoadPoolAsync(req.Capability, ct);
        var outcome = ResourceMatcher.Evaluate(pool, constraints, time);
        var candidates = await registry.ToDtosAsync(outcome.Candidates, ct);
        return new DiscoverResponse(req.Capability, candidates, outcome.Excluded);
    }
}
