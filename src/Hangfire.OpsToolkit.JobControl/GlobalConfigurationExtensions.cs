using Hangfire.Common;

namespace Hangfire.OpsToolkit.JobControl;

/// <summary>
/// Server plane of job control, following the Hangfire extension convention (cf. Hangfire.Console's
/// <c>UseConsole()</c>): chain onto the <c>IGlobalConfiguration</c> passed to <c>AddHangfire</c>. The
/// HTTP plane (API + UI) is mapped separately via <see cref="JobControlEndpoints.MapJobControl"/>.
/// </summary>
public static class GlobalConfigurationExtensions
{
    /// <summary>
    /// Registers <see cref="DisabledRecurringJobFilter"/> globally at order -1 — before a method-level
    /// <c>[DisableConcurrentExecution]</c> (default order -1; Global scope sorts ahead of Method scope
    /// at equal order) takes its distributed lock — and <see cref="CancellationOutcomeFilter"/> (default
    /// order; only <c>OnPerformed</c> is used, so ordering relative to other filters doesn't matter).
    /// Idempotent: <c>GlobalJobFilters</c> is process-global, so any prior instance of either filter is
    /// replaced rather than stacked — safe when the host is built more than once in a process (e.g.
    /// integration tests).
    /// </summary>
    public static IGlobalConfiguration UseJobControl(this IGlobalConfiguration configuration, JobControlOptions? options = null)
    {
        var jobControlOptions = options ?? new JobControlOptions();

        replace(new DisabledRecurringJobFilter(), order: -1);
        replace(new CancellationOutcomeFilter(jobControlOptions.AuditMaxEntries), order: null);

        return configuration;

        static void replace<TFilter>(TFilter filter, int? order) where TFilter : class
        {
            var existing = GlobalJobFilters.Filters
                .Select(f => f.Instance)
                .OfType<TFilter>()
                .FirstOrDefault();
            if (existing != null)
                GlobalJobFilters.Filters.Remove(existing);

            if (order.HasValue) GlobalJobFilters.Filters.Add(filter, order.Value);
            else GlobalJobFilters.Filters.Add(filter);
        }
    }
}
