using Hangfire.Logging;
using Hangfire.Server;

namespace Hangfire.OpsToolkit.JobControl;

/// <summary>
/// Acknowledgment half of the processing-job cancellation protocol: the
/// moment a job's execution observably stops because of a governed cancel request — or the honest record
/// that it didn't — recorded as an audit entry. Registered by
/// <see cref="GlobalConfigurationExtensions.UseJobControl"/>, same idempotent-guard pattern as
/// <see cref="DisabledRecurringJobFilter"/>.
///
/// <c>OnPerforming</c> is a no-op: there is nothing to decide before the body runs. <c>OnPerformed</c>
/// fires with the exception in context even when the body threw — including <see cref="JobAbortedException"/>
/// (mechanic #9) — which is the seam that makes a real acknowledgment observable in-process, at the
/// moment the body stopped.
/// </summary>
public sealed class CancellationOutcomeFilter : IServerFilter
{
    private readonly int _auditMaxEntries;

    public CancellationOutcomeFilter(int auditMaxEntries)
    {
        _auditMaxEntries = auditMaxEntries;
    }

    public void OnPerforming(PerformingContext context)
    {
    }

    public void OnPerformed(PerformedContext context)
    {
        // Never throws: any failure recording an ack must not affect the worker's own exception
        // handling/rethrow, which runs immediately after this filter returns (same rule §2.3 states
        // explicitly). Broad catch is deliberate — every failure mode here is equally "log and move on".
        try
        {
            record(context);
        }
        catch (Exception ex)
        {
            LogProvider.GetLogger(typeof(CancellationOutcomeFilter))
                .ErrorException("Failed to record a cancellation acknowledgment.", ex);
        }
    }

    private void record(PerformedContext context)
    {
        var jobId = context.BackgroundJob.Id;
        var marker = CancellationRequestStore.Read(context.Connection, jobId);

        if (marker is null)
        {
            // No cancel request of ours preceded this abort — someone else changed the job's state away
            // from Processing (built-in dashboard Delete, a raw BackgroundJob.Delete call, a state
            // clobber from application code). The actor is unknowable at this seam (job filters carry no
            // HTTP context), so the event still enters the activity
            // feed as `abort-observed` instead of vanishing silently. Only bother reading the
            // RecurringJobId correlation (§5) on this rare path, not on every ordinary completion below.
            if (context.Exception is JobAbortedException)
            {
                append(jobId, "abort-observed", actor: "unknown", reason: null,
                    recurringJobIdDetail(AuditStore.TryGetRecurringJobId(context.Connection, jobId)));
            }
            return;
        }

        // Outcome is always "ok" here — the ack write itself either succeeds or the catch above logs it;
        // the job body's actual fate is the classification in Detail["Result"], per §2.3.
        Dictionary<string, string> detail;
        switch (context.Exception)
        {
            case JobAbortedException:
                var elapsedMs = Math.Max(0, (long)(DateTime.UtcNow - marker.At).TotalMilliseconds);
                detail = new Dictionary<string, string> { ["Result"] = "aborted", ["ElapsedMs"] = elapsedMs.ToString() };
                break;

            case null:
                // The body ran to completion despite the request — mechanic #6 guarantees the job stays
                // Deleted regardless, so this is evidence the job body isn't cancellation-safe, not a
                // late completion clobbering the cancel.
                detail = new Dictionary<string, string> { ["Result"] = "completed-anyway" };
                break;

            case OperationCanceledException:
                // Deliberate exclusion (§2.3): a shutdown-triggered OperationCanceledException (not an
                // abort) is rethrown as-is by Hangfire and the job is requeued — it will run again, so an
                // ack here would be wrong. The rerun's own OnPerformed settles it instead.
                return;

            default:
                detail = new Dictionary<string, string> { ["Result"] = "faulted", ["Exception"] = context.Exception.GetType().Name };
                break;
        }

        var recurringJobId = AuditStore.TryGetRecurringJobId(context.Connection, jobId);
        if (recurringJobId is not null) detail["RecurringJobId"] = recurringJobId;
        append(jobId, "cancel-ack", marker.By, marker.Reason, detail);
    }

    private static Dictionary<string, string>? recurringJobIdDetail(string? recurringJobId)
        => recurringJobId is null ? null : new Dictionary<string, string> { ["RecurringJobId"] = recurringJobId };

    private void append(string jobId, string action, string actor, string? reason, IReadOnlyDictionary<string, string>? detail)
        => AuditStore.Append(JobStorage.Current, new AuditEntry(
            AuditEntry.CurrentVersion, DateTime.UtcNow, actor, action, jobId, reason, "ok", detail), _auditMaxEntries);
}
