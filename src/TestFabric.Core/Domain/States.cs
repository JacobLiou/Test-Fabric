namespace TestFabric.Core.Domain;

/// <summary>
/// 状态机定义（FR-07）。状态以字符串存储于数据库，常量集中在此。
/// 迁移合法性由 SQL 条件更新（WHERE operational_state = ...）在数据库层保证；
/// 此迁移表用于文档化、防御性校验与单元测试。
/// Administrative（Enabled/Disabled/Maintenance）与 Connectivity（Online/Offline）
/// 是与 Operational 正交的两个独立维度，不在此表内。
/// </summary>
public static class States
{
    public static class Administrative
    {
        public const string Enabled = "Enabled";
        public const string Disabled = "Disabled";
        public const string Maintenance = "Maintenance";
    }

    public static class Operational
    {
        public const string Unknown = "Unknown";
        public const string Available = "Available";
        public const string Busy = "Busy";
        public const string Recovering = "Recovering";
        public const string Error = "Error";

        /// <summary>允许的 Operational 状态迁移表。</summary>
        public static readonly IReadOnlyDictionary<string, string[]> AllowedTransitions =
            new Dictionary<string, string[]>
            {
                [Unknown] = [Available, Error],
                [Available] = [Busy, Recovering, Error],
                [Busy] = [Recovering, Error],
                [Recovering] = [Available, Error],
                [Error] = [Recovering, Available],
            };
    }

    public static class Health
    {
        public const string Unknown = "Unknown";
        public const string Healthy = "Healthy";
        public const string Degraded = "Degraded";
    }

    public static class Connectivity
    {
        public const string Unknown = "Unknown";
        public const string Online = "Online";
        public const string Offline = "Offline";
    }

    public static class LeaseStatus
    {
        public const string Active = "Active";
        public const string Expired = "Expired";
        public const string Closed = "Closed";
    }

    public static bool IsAllowedOperationalTransition(string from, string to)
        => Operational.AllowedTransitions.TryGetValue(from, out var targets) && targets.Contains(to);
}
