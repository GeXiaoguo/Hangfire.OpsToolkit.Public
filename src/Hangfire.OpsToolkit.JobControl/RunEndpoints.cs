using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hangfire.OpsToolkit.JobControl;

/// <summary>
/// Body shared by the three run mutation endpoints — <c>cancel</c>/<c>requeue</c>/<c>delete</c> (§2.1,
/// §3.2). <see cref="Reason"/> is required for cancel, optional for requeue/delete;
/// <see cref="ExpectedState"/> is required for all three — the state the UI saw, surfaced from
/// mechanic #10's <c>ChangeState</c> assertion so a job that moved in between comes back as a 409
/// refusal rather than a blind mutation of the wrong thing.
/// </summary>
public sealed record RunActionRequest(string? Reason, string? ExpectedState);

/// <summary>Per-state counts driving the Runs dashboard's tab badges (<see cref="StatisticsDto"/>, trimmed to the tabs this page has).</summary>
public sealed record RunStats(long Enqueued, long Scheduled, long Processing, long Succeeded, long Failed, long Deleted, long Servers);

public sealed record RunQueueSummary(string Name, long Length, long? Fetched);

public sealed record RunEnqueuedSummary(string Id, string? JobDisplayName, DateTime? EnqueuedAt);

public sealed record RunProcessingSummary(string Id, string? JobDisplayName, string? ServerId, DateTime? StartedAt);

public sealed record RunScheduledSummary(string Id, string? JobDisplayName, DateTime EnqueueAt);

public sealed record RunSucceededSummary(string Id, string? JobDisplayName, DateTime? SucceededAt, long? DurationMs);

public sealed record RunFailedSummary(string Id, string? JobDisplayName, DateTime? FailedAt, string? ExceptionType, string? ExceptionMessage);

/// <summary>
/// <see cref="Cancelled"/> and the fields after it come from the <c>JobControl.CancelRequested</c> job
/// parameter (see <see cref="CancellationRequestStore"/>), not from Hangfire's own <c>DeletedJobDto</c> —
/// that's what lets the Deleted tab distinguish a governed cancel from a plain delete (§2.4/§3.4).
/// </summary>
public sealed record RunDeletedSummary(
    string Id, string? JobDisplayName, DateTime? DeletedAt,
    bool Cancelled, string? CancelledBy, DateTime? CancelledAt, string? CancelReason);

public sealed record RunServerSummary(string Name, int WorkersCount, DateTime StartedAt, IList<string> Queues, DateTime? Heartbeat);

public sealed record RunStateHistoryEntry(string StateName, string? Reason, DateTime CreatedAt, IDictionary<string, string>? Data);

/// <summary>
/// Drives the drawer: <see cref="JobDetailsDto"/> trimmed to what an operator needs (invocation display
/// name, parameters, and the full per-run state history) — <see cref="JobDetailsDto.InvocationData"/>
/// itself is Hangfire's serialized-args wire format, not something worth exposing raw.
/// </summary>
public sealed record RunJobDetails(
    string Id,
    string? JobDisplayName,
    IDictionary<string, string>? Properties,
    DateTime? CreatedAt,
    DateTime? ExpireAt,
    IReadOnlyList<RunStateHistoryEntry> History);

/// <summary>
/// HTTP plane of the Job Runs dashboard: a JSON facade over <see cref="IMonitoringApi"/> (the same read
/// plane the built-in Hangfire dashboard itself renders from) plus its bundled operator UI, the
/// cancel-request → abort → acknowledge protocol, and the
/// governed requeue/delete mutations (§3.2).
/// </summary>
public static class RunEndpoints
{
    public const string DefaultApiBase = "/hangfire/api/runs";
    public const string DefaultUiPath = "/hangfire/job-control/runs";

    private const string UiResourceName = "Hangfire.OpsToolkit.JobControl.wwwroot.runs.html";
    private const string ApiBasePlaceholder = "{{API_BASE}}";
    private const string OwnUiPathPlaceholder = "{{OWN_UI_PATH}}";
    private const string RecurringUiPathPlaceholder = "{{RECURRING_UI_PATH}}";

    // Independent of JobControlOptions.RunsDefaultPageSize (the default when a caller doesn't specify
    // one); this bounds what a caller CAN ask for even when specifying a count — same split as
    // JobControlEndpoints.AuditReadLimitHardCap.
    private const int RunsReadLimitHardCap = 500;

    /// <summary>API only — for hosts that bring their own frontend. Mirrors <see cref="JobControlEndpoints.MapJobControlApi"/>.</summary>
    public static JobControlApiGroups MapJobRunsApi(
        this IEndpointRouteBuilder endpoints,
        string viewPolicy,
        string managePolicy,
        string apiBase,
        JobControlOptions? options = null)
    {
        var jobControlOptions = options ?? new JobControlOptions();
        var view = endpoints.MapGroup(apiBase).RequireAuthorization(viewPolicy);
        var manage = endpoints.MapGroup(apiBase).RequireAuthorization(managePolicy);

        view.MapGet("/stats", () =>
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            var stats = monitor.GetStatistics();
            return Results.Ok(new RunStats(
                stats.Enqueued, stats.Scheduled, stats.Processing, stats.Succeeded, stats.Failed, stats.Deleted, stats.Servers));
        });

        // Drives the Queued tab's queue picker; per-queue job lists come from /enqueued?queue=.
        view.MapGet("/queues", () =>
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            var queues = monitor.Queues();
            return Results.Ok(queues.Select(q => new RunQueueSummary(q.Name, q.Length, q.Fetched)).ToList());
        });

        view.MapGet("/enqueued", (string? queue, int? from, int? count) =>
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            // No queue named yet (first load of the tab) — default to whatever /queues would list first,
            // the same "pick one so the tab isn't empty" behavior as the built-in dashboard.
            var effectiveQueue = queue ?? monitor.Queues().FirstOrDefault()?.Name;
            if (effectiveQueue is null) return Results.Ok(Array.Empty<RunEnqueuedSummary>());

            var (pageFrom, pageCount) = clampPage(from, count, jobControlOptions.RunsDefaultPageSize);
            var jobs = monitor.EnqueuedJobs(effectiveQueue, pageFrom, pageCount);
            return Results.Ok(jobs
                .Select(pair => new RunEnqueuedSummary(pair.Key, jobDisplayName(pair.Value.Job, pair.Value.LoadException), pair.Value.EnqueuedAt))
                .ToList());
        });

        view.MapGet("/processing", (int? from, int? count) =>
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            var (pageFrom, pageCount) = clampPage(from, count, jobControlOptions.RunsDefaultPageSize);
            var jobs = monitor.ProcessingJobs(pageFrom, pageCount);
            return Results.Ok(jobs
                .Select(pair => new RunProcessingSummary(pair.Key, jobDisplayName(pair.Value.Job, pair.Value.LoadException), pair.Value.ServerId, pair.Value.StartedAt))
                .ToList());
        });

        view.MapGet("/scheduled", (int? from, int? count) =>
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            var (pageFrom, pageCount) = clampPage(from, count, jobControlOptions.RunsDefaultPageSize);
            var jobs = monitor.ScheduledJobs(pageFrom, pageCount);
            return Results.Ok(jobs
                .Select(pair => new RunScheduledSummary(pair.Key, jobDisplayName(pair.Value.Job, pair.Value.LoadException), pair.Value.EnqueueAt))
                .ToList());
        });

        view.MapGet("/succeeded", (int? from, int? count) =>
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            var (pageFrom, pageCount) = clampPage(from, count, jobControlOptions.RunsDefaultPageSize);
            var jobs = monitor.SucceededJobs(pageFrom, pageCount);
            return Results.Ok(jobs
                .Select(pair => new RunSucceededSummary(pair.Key, jobDisplayName(pair.Value.Job, pair.Value.LoadException), pair.Value.SucceededAt, pair.Value.TotalDuration))
                .ToList());
        });

        view.MapGet("/failed", (int? from, int? count) =>
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            var (pageFrom, pageCount) = clampPage(from, count, jobControlOptions.RunsDefaultPageSize);
            var jobs = monitor.FailedJobs(pageFrom, pageCount);
            return Results.Ok(jobs
                .Select(pair => new RunFailedSummary(pair.Key, jobDisplayName(pair.Value.Job, pair.Value.LoadException), pair.Value.FailedAt, pair.Value.ExceptionType, pair.Value.ExceptionMessage))
                .ToList());
        });

        view.MapGet("/deleted", (int? from, int? count) =>
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            var (pageFrom, pageCount) = clampPage(from, count, jobControlOptions.RunsDefaultPageSize);
            var jobs = monitor.DeletedJobs(pageFrom, pageCount);

            // One GetJobParameter per row (a PK lookup) — trivial at page-size volume — is what
            // distinguishes a governed cancel from a plain delete without the drawer (§3.1, §3.4).
            using var connection = JobStorage.Current.GetConnection();
            return Results.Ok(jobs
                .Select(pair =>
                {
                    var marker = CancellationRequestStore.Read(connection, pair.Key);
                    return new RunDeletedSummary(
                        pair.Key, jobDisplayName(pair.Value.Job, pair.Value.LoadException), pair.Value.DeletedAt,
                        Cancelled: marker is not null, marker?.By, marker?.At, marker?.Reason);
                })
                .ToList());
        });

        view.MapGet("/servers", () =>
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            var servers = monitor.Servers();
            return Results.Ok(servers
                .Select(s => new RunServerSummary(s.Name, s.WorkersCount, s.StartedAt, s.Queues, s.Heartbeat))
                .ToList());
        });

        view.MapGet("/{id}", (string id) =>
        {
            var monitor = JobStorage.Current.GetMonitoringApi();
            JobDetailsDto? details;
            try
            {
                details = monitor.JobDetails(id);
            }
            catch (FormatException)
            {
                // Verified empirically against Hangfire.PostgreSql 1.20.13 (this repo's own test/demo
                // dependency): its JobDetails does an unguarded Convert.ToInt64(jobId) internally (job
                // ids there are auto-increment integers, stringified) and throws FormatException for an
                // id that never had that shape, rather than returning null like a "well-formed but
                // missing" id does. Same 404 outcome either way from the operator's side.
                details = null;
            }
            if (details is null) return Results.NotFound(id);

            var history = (details.History ?? new List<StateHistoryDto>())
                .Select(h => new RunStateHistoryEntry(h.StateName, h.Reason, h.CreatedAt, h.Data))
                .ToList();
            return Results.Ok(new RunJobDetails(
                id, jobDisplayName(details.Job, details.LoadException), details.Properties, details.CreatedAt, details.ExpireAt, history));
        });

        // Thin passthrough over the same AuditStore the Recurring page's own /audit endpoint reads —
        // same store and schema. Scoped under this page's
        // own API base so the Runs drawer's cancel panel (§3.4: "resolved by polling that job's audit
        // entries") doesn't need to know the Recurring page's API base just to poll one job's history.
        view.MapGet("/{id}/audit", (string id, int? limit) =>
        {
            var effectiveLimit = Math.Clamp(limit ?? jobControlOptions.AuditDefaultReadLimit, 1, JobControlEndpoints.AuditReadLimitHardCap);
            try
            {
                return Results.Ok(AuditStore.Read(JobStorage.Current, effectiveLimit, id));
            }
            catch (NotSupportedException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status501NotImplemented);
            }
        });

        // Shared by cancel/requeue/delete below — one audit-append shape per action name.
        void appendAudit(string action, string actor, string jobId, string? reason, string outcome, IReadOnlyDictionary<string, string>? detail)
            => AuditStore.Append(JobStorage.Current, new AuditEntry(
                AuditEntry.CurrentVersion, DateTime.UtcNow, actor, action, jobId, reason, outcome, detail),
                jobControlOptions.AuditMaxEntries);

        // §2.1: request → abort → acknowledge. Reason and expectedState are both required — expectedState
        // is the state the UI saw (Enqueued/Scheduled/Processing), surfaced from mechanic #10's
        // ChangeState assertion so a job that moved in between comes back as a 409 refusal, not a blind
        // kill of the wrong thing.
        manage.MapPost("/{id}/cancel", (string id, RunActionRequest request, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
                return Results.BadRequest("A reason is required to cancel a job.");
            if (string.IsNullOrWhiteSpace(request.ExpectedState))
                return Results.BadRequest("expectedState is required.");

            var who = JobControlEndpoints.actor(http, jobControlOptions);

            JobData? jobData;
            using (var connection = JobStorage.Current.GetConnection())
            {
                jobData = tryGetJobData(connection, id);
            }

            if (jobData is null)
            {
                appendAudit("cancel", who, id, request.Reason, "not-found", detail: null);
                return Results.NotFound(id);
            }

            if (!string.Equals(jobData.State, request.ExpectedState, StringComparison.OrdinalIgnoreCase))
            {
                appendAudit("cancel", who, id, request.Reason, "wrong-state",
                    new Dictionary<string, string> { ["CurrentState"] = jobData.State ?? "" });
                return Results.Conflict(new { currentState = jobData.State });
            }

            // Reason rides on the state itself (not just the audit trail) — even the built-in
            // dashboard's job detail page then shows who/why in the state history (§2.1 point 1).
            var stateReason = $"Cancelled by {who}: {request.Reason}";
            var client = new BackgroundJobClient(JobStorage.Current);
            var changed = client.ChangeState(id, new DeletedState { Reason = stateReason }, request.ExpectedState);

            if (!changed)
            {
                // Lost a race between the state read above and the change itself (job moved again in
                // between) — refetch for an honest current-state 409 rather than a bare failure.
                string? current;
                using (var connection = JobStorage.Current.GetConnection())
                {
                    current = tryGetJobData(connection, id)?.State;
                }
                appendAudit("cancel", who, id, request.Reason, "wrong-state",
                    new Dictionary<string, string> { ["CurrentState"] = current ?? "unknown" });
                return Results.Conflict(new { currentState = current });
            }

            var okDetail = new Dictionary<string, string> { ["FromState"] = request.ExpectedState };
            var displayName = jobDisplayName(jobData.Job, jobData.LoadException);
            if (displayName is not null) okDetail["JobDisplayName"] = displayName;

            using (var connection = JobStorage.Current.GetConnection())
            {
                // Step 2 (§2.1): only a Processing cancel has a running body to acknowledge —
                // queued/scheduled cancels are complete the instant the state change lands (mechanics
                // #7, #8), so writing a marker for those would leave a permanent phantom (nothing will
                // ever acknowledge it).
                if (string.Equals(request.ExpectedState, ProcessingState.StateName, StringComparison.OrdinalIgnoreCase))
                    CancellationRequestStore.Write(connection, id, who, DateTime.UtcNow, request.Reason);

                var recurringJobId = AuditStore.TryGetRecurringJobId(connection, id);
                if (recurringJobId is not null) okDetail["RecurringJobId"] = recurringJobId;
            }

            appendAudit("cancel", who, id, request.Reason, "ok", okDetail);
            return Results.Ok();
        });

        // §3.2: requeue is a rescue lever on Enqueued (a lost/stuck queue entry), "Run now" on Scheduled
        // (same wire action, different UI label), a deliberate re-execution on Succeeded/Failed, and the
        // second half of the governed "stop and rerun" sequence on Deleted (requeue-from-Deleted after a
        // cancel has been requested/acknowledged — the UI's own job is gating that on ack, §3.2's
        // requeue-before-ack guard; the API itself never hard-blocks it, since a dead-worker no-ack would
        // otherwise strand the job forever).
        manage.MapPost("/{id}/requeue", (string id, RunActionRequest request, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.ExpectedState))
                return Results.BadRequest("expectedState is required.");

            var who = JobControlEndpoints.actor(http, jobControlOptions);

            // §3.3: requeue of a job the caller claims is Processing is refused outright — unlike the
            // expectedState mismatch below (a race the caller couldn't have known about), this rejects
            // exactly what the caller asked for, because honoring it risks a concurrent double-execution
            // against whatever body might still be running.
            if (string.Equals(request.ExpectedState, ProcessingState.StateName, StringComparison.OrdinalIgnoreCase))
            {
                appendAudit("requeue", who, id, request.Reason, "processing-rejected", detail: null);
                return Results.Conflict(new { currentState = ProcessingState.StateName });
            }

            JobData? jobData;
            using (var connection = JobStorage.Current.GetConnection())
            {
                jobData = tryGetJobData(connection, id);
            }

            if (jobData is null)
            {
                appendAudit("requeue", who, id, request.Reason, "not-found", detail: null);
                return Results.NotFound(id);
            }

            if (!string.Equals(jobData.State, request.ExpectedState, StringComparison.OrdinalIgnoreCase))
            {
                appendAudit("requeue", who, id, request.Reason, "wrong-state",
                    new Dictionary<string, string> { ["CurrentState"] = jobData.State ?? "" });
                return Results.Conflict(new { currentState = jobData.State });
            }

            var client = new BackgroundJobClient(JobStorage.Current);
            var changed = client.Requeue(id, request.ExpectedState);

            if (!changed)
            {
                string? current;
                using (var connection = JobStorage.Current.GetConnection())
                {
                    current = tryGetJobData(connection, id)?.State;
                }
                appendAudit("requeue", who, id, request.Reason, "wrong-state",
                    new Dictionary<string, string> { ["CurrentState"] = current ?? "unknown" });
                return Results.Conflict(new { currentState = current });
            }

            var detail = new Dictionary<string, string> { ["FromState"] = request.ExpectedState };
            var displayName = jobDisplayName(jobData.Job, jobData.LoadException);
            if (displayName is not null) detail["JobDisplayName"] = displayName;

            using (var connection = JobStorage.Current.GetConnection())
            {
                // §2.3 marker lifecycle: a cancelled-then-requeued job must not carry the old request
                // into its next run, or that run's own ordinary completion would record a phantom
                // completed-anyway ack.
                CancellationRequestStore.Clear(connection, id);

                var recurringJobId = AuditStore.TryGetRecurringJobId(connection, id);
                if (recurringJobId is not null) detail["RecurringJobId"] = recurringJobId;
            }

            appendAudit("requeue", who, id, request.Reason, "ok", detail);
            return Results.Ok();
        });

        // §3.2: delete is for a terminal run with no body left to stop — Succeeded or Failed only (a
        // queued/scheduled/processing job is stopped via cancel, which carries the reasoned intent a
        // running body needs; delete here is pure history removal). Distinct audit action `delete-run`
        // from the recurring page's own `delete` — different target kind, and old entries must stay
        // unambiguous (§5).
        manage.MapPost("/{id}/delete", (string id, RunActionRequest request, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.ExpectedState))
                return Results.BadRequest("expectedState is required.");
            if (!string.Equals(request.ExpectedState, SucceededState.StateName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.ExpectedState, FailedState.StateName, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("expectedState must be Succeeded or Failed — a queued or running job is stopped via cancel, not delete.");

            var who = JobControlEndpoints.actor(http, jobControlOptions);

            JobData? jobData;
            IDictionary<string, string>? stateData;
            using (var connection = JobStorage.Current.GetConnection())
            {
                jobData = tryGetJobData(connection, id);
                // Captured before the state change below, while the job is still in the state we're
                // about to delete it from — reading it afterwards would see Deleted's own state data
                // instead of the Failed exception details this snapshot exists to preserve.
                stateData = jobData is null ? null : connection.GetStateData(id)?.Data;
            }

            if (jobData is null)
            {
                appendAudit("delete-run", who, id, request.Reason, "not-found", detail: null);
                return Results.NotFound(id);
            }

            if (!string.Equals(jobData.State, request.ExpectedState, StringComparison.OrdinalIgnoreCase))
            {
                appendAudit("delete-run", who, id, request.Reason, "wrong-state",
                    new Dictionary<string, string> { ["CurrentState"] = jobData.State ?? "" });
                return Results.Conflict(new { currentState = jobData.State });
            }

            var client = new BackgroundJobClient(JobStorage.Current);
            var changed = client.Delete(id, request.ExpectedState);

            if (!changed)
            {
                string? current;
                using (var connection = JobStorage.Current.GetConnection())
                {
                    current = tryGetJobData(connection, id)?.State;
                }
                appendAudit("delete-run", who, id, request.Reason, "wrong-state",
                    new Dictionary<string, string> { ["CurrentState"] = current ?? "unknown" });
                return Results.Conflict(new { currentState = current });
            }

            // The entry may end up the only surviving record once the job row expires — same rationale
            // as the recurring page's own delete snapshot (+exception summary for Failed).
            var detail = new Dictionary<string, string> { ["FromState"] = request.ExpectedState };
            var displayName = jobDisplayName(jobData.Job, jobData.LoadException);
            if (displayName is not null) detail["JobDisplayName"] = displayName;
            if (string.Equals(request.ExpectedState, FailedState.StateName, StringComparison.OrdinalIgnoreCase) && stateData is not null)
            {
                if (stateData.TryGetValue("ExceptionType", out var excType)) detail["ExceptionType"] = excType;
                if (stateData.TryGetValue("ExceptionMessage", out var excMessage)) detail["ExceptionMessage"] = excMessage;
            }

            using (var connection = JobStorage.Current.GetConnection())
            {
                var recurringJobId = AuditStore.TryGetRecurringJobId(connection, id);
                if (recurringJobId is not null) detail["RecurringJobId"] = recurringJobId;
            }

            appendAudit("delete-run", who, id, request.Reason, "ok", detail);
            return Results.Ok();
        });

        return new JobControlApiGroups(view, manage);
    }

    /// <summary>
    /// Bundled UI only. <paramref name="recurringUiPath"/> feeds the shared cross-nav header's link back
    /// to the Recurring Jobs page — see <see cref="JobControlEndpoints.MapJobControlUi"/> for the mirror.
    /// Prefer <see cref="JobControlEndpoints.MapJobControl"/>, which wires both pages together and gates
    /// this one with the view policy automatically.
    /// </summary>
    public static RouteHandlerBuilder MapJobRunsUi(
        this IEndpointRouteBuilder endpoints,
        string uiPath = DefaultUiPath,
        string apiBase = DefaultApiBase,
        string recurringUiPath = JobControlEndpoints.DefaultUiPath)
    {
        var html = loadUiTemplate()
            .Replace(ApiBasePlaceholder, apiBase)
            .Replace(OwnUiPathPlaceholder, uiPath)
            .Replace(RecurringUiPathPlaceholder, recurringUiPath);
        return endpoints.MapGet(uiPath, () => Results.Content(html, "text/html"));
    }

    private static (int From, int Count) clampPage(int? from, int? count, int defaultCount)
        => (Math.Max(0, from ?? 0), Math.Clamp(count ?? defaultCount, 1, RunsReadLimitHardCap));

    // Same Hangfire.PostgreSql 1.20.13 id-shape quirk noted on the GET /{id} handler above
    // (Convert.ToInt64(jobId) unguarded internally) — GetJobData shares the same storage-layer id
    // parsing, so a non-numeric id must be treated as "doesn't exist" here too, not an unhandled 500.
    private static JobData? tryGetJobData(IStorageConnection connection, string jobId)
    {
        try
        {
            return connection.GetJobData(jobId);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // Generalizes JobControlEndpoints.jobDisplayName (which is keyed off RecurringJobDto specifically)
    // across the several Hangfire monitoring DTOs that each carry their own Job/LoadException pair with
    // no shared interface between them.
    private static string? jobDisplayName(Job? job, JobLoadException? loadException) => (job, loadException) switch
    {
        ({ } j, _) => $"{j.Type.Name}.{j.Method.Name}",
        (_, { InnerException: { } inner }) => inner.Message,
        (_, { } le) => le.Message,
        _ => null,
    };

    private static string loadUiTemplate()
    {
        var assembly = typeof(RunEndpoints).Assembly;
        using var stream = assembly.GetManifestResourceStream(UiResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{UiResourceName}' not found — check the EmbeddedResource item in the csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
