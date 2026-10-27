using TestFabric.Contracts;
using TestFabric.Core.Domain;

namespace TestFabric.Core.Matching;

public record MatchOutcome(IReadOnlyList<Resource> Candidates, IReadOnlyList<ExcludedResource> Excluded);

/// <summary>
/// 资源匹配器：对已加载的资源池执行「可分配过滤 + 约束求值」，产出候选集与排除原因。
/// Discovery 与 Acquire 共用同一套逻辑，保证「查到的」与「能分到的」一致（FR-03 / FR-04）。
/// </summary>
public static class ResourceMatcher
{
    public static MatchOutcome Evaluate(
        IEnumerable<Resource> pool,
        IReadOnlyDictionary<string, ConstraintSpec> constraints,
        TimeProvider time)
    {
        var candidates = new List<Resource>();
        var excluded = new List<ExcludedResource>();

        foreach (var r in pool)
        {
            if (EvaluateOne(r, constraints, time, out var reason)) candidates.Add(r);
            else excluded.Add(new ExcludedResource(r.ResourceId, reason!));
        }

        // 分配策略（FR-04-8）：最早空闲优先（从未用过的排最前），同条件按 Resource ID 稳定排序。
        candidates.Sort(static (a, b) =>
        {
            var byIdle = Nullable.Compare(a.LastReleasedAt, b.LastReleasedAt);
            return byIdle != 0 ? byIdle : string.CompareOrdinal(a.ResourceId, b.ResourceId);
        });
        return new MatchOutcome(candidates, excluded);
    }

    private static bool EvaluateOne(
        Resource r,
        IReadOnlyDictionary<string, ConstraintSpec> constraints,
        TimeProvider time,
        out string? reason)
    {
        if (r.AdministrativeState == States.Administrative.Disabled) { reason = "resource is disabled"; return false; }
        if (r.AdministrativeState == States.Administrative.Maintenance) { reason = "resource is under maintenance"; return false; }
        if (r.ConnectivityState == States.Connectivity.Offline) { reason = "resource is offline"; return false; }
        if (r.HealthState != States.Health.Healthy) { reason = $"health state is {r.HealthState}"; return false; }
        if (r.OperationalState != States.Operational.Available) { reason = $"operational state is {r.OperationalState}"; return false; }

        // FR-04-3：校准过期是硬性门槛——无论约束是否声明，都不得分配（E2E-05）
        var calib = r.CalibrationState(time);
        if (calib == CalibrationInfo.Expired)
        {
            reason = $"calibration expired (due {r.CalibrationDueDate:yyyy-MM-dd})";
            return false;
        }
        if (constraints.TryGetValue("calibrationValid", out var calibConstraint))
        {
            var calibValid = calib is CalibrationInfo.Valid or CalibrationInfo.Expiring;
            if (!ConstraintEvaluator.TryEvaluate("calibrationValid", calibConstraint, calibValid, out var calibReason))
            {
                reason = $"calibration {calib.ToLowerInvariant()} ({calibReason})";
                return false;
            }
        }

        if (r.CapabilityProperties is null) { reason = "capability is not provided by this resource"; return false; }

        foreach (var (name, spec) in constraints)
        {
            if (ConstraintParser.BuiltinProperties.Contains(name))
            {
                object? value = name switch
                {
                    "healthState" => r.HealthState,
                    "operationalState" => r.OperationalState,
                    "administrativeState" => r.AdministrativeState,
                    _ => null, // calibrationValid 已在上面处理
                };
                if (value is null) continue;
                if (!ConstraintEvaluator.TryEvaluate(name, spec, value, out var bReason)) { reason = bReason!; return false; }
                continue;
            }

            if (!r.CapabilityProperties.TryGetValue(name, out var propertyValue))
            {
                reason = $"property '{name}' is not declared by the resource";
                return false;
            }
            if (!ConstraintEvaluator.TryEvaluate(name, spec, propertyValue, out var pReason)) { reason = pReason!; return false; }
        }

        reason = null;
        return true;
    }
}
