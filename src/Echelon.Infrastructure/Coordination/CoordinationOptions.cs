namespace Echelon.Infrastructure.Coordination;

/// <summary>
/// Which backend carries the two things replicas share: the permission cache and the job lease.
/// </summary>
/// <remarks>
/// <para>
/// Neither is a source of truth. The permission stamp is a hash of the stored rules, so losing the
/// cache costs three reads and never correctness; the lease exists to stop N replicas each running
/// the same nightly pass, and the passes are idempotent. That is why this is selectable at all -
/// the backend is an efficiency and contention choice, not the place the data lives.
/// </para>
/// <para>
/// What it is not is free: pick <see cref="CoordinationProviders.Memory"/> with more than one
/// replica and every replica leads its own archive cycle, every one sweeps the tracker, and a
/// revoked permission lingers in the others for the stamp's lifetime. So the provider is named
/// explicitly and <c>memory</c> additionally requires the operator to state that the deployment is
/// a single instance - the same shape as <c>DataProtection:AllowUnprotectedKeys</c>: accepting a
/// risk is allowed, arriving at one unawares is not.
/// </para>
/// </remarks>
public class CoordinationOptions
{
    /// <summary>Configuration section carrying these settings.</summary>
    public const string SectionName = "Coordination";

    /// <summary>
    /// Which backend to use. Matched in canonical form, so <c>Redis</c> and <c>redis</c> are the
    /// same provider.
    /// </summary>
    public string Provider { get; set; } = CoordinationProviders.Redis;

    /// <summary>
    /// The operator's assertion that this deployment runs exactly one replica, which is what makes
    /// <see cref="CoordinationProviders.Memory"/> correct. Nothing here can verify it: a second
    /// replica would hold its own in-process lease and believe it leads.
    /// </summary>
    public bool SingleInstance { get; set; }
}

/// <summary>The coordination backends this build knows how to register.</summary>
/// <remarks>
/// Canonical, lower-case keys, matched through <c>ProviderKey.Normalize</c> - the same rule the
/// VCS and tracker registries use, so a value typed into compose does not have to match case.
/// </remarks>
public static class CoordinationProviders
{
    /// <summary>Everything shared lives in this process. Correct for exactly one replica.</summary>
    public const string Memory = "memory";

    /// <summary>Cache and lease in Redis. The default, and the only one that needs another service.</summary>
    public const string Redis = "redis";

    /// <summary>Every provider this build registers, for validation and for error messages.</summary>
    public static readonly IReadOnlyList<string> All = [Memory, Redis];
}
