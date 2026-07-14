# Operator audit trail

The audit trail records actions performed through the toolkit's recurring-job and Job Runs surfaces. It is intended for human-volume operational events, not every job state transition.

## Recorded actions

Recurring-job actions include `disable`, `enable`, `trigger`, and `delete`. Run actions include `cancel`, `cancel-ack`, `requeue`, and `delete-run`. Entries contain a UTC timestamp, actor, action, job identifier, reason, outcome, and action-specific details.

The cancellation acknowledgment distinguishes:

- `aborted`: the running body observed cancellation and stopped.
- `completed-anyway`: the body completed without cooperating with cancellation.
- `faulted`: the body failed for another reason after cancellation was requested.

Run entries include `RecurringJobId` when Hangfire stamped one on the background job, allowing recurring-job history to include related run operations.

## Storage and retention

Entries are versioned JSON values in the Hangfire list `jobcontrol:audit`. The list is count-capped by `JobControlOptions.AuditMaxEntries` (10,000 by default), so the feature requires no host database schema or cleanup job.

Malformed or future-format entries are skipped during reads instead of breaking the history view. Retention uses read-verified removal rather than provider-specific `TrimList` direction semantics.

Disable and enable update their control flag and audit entry in one Hangfire storage transaction. Other actions use the strongest atomicity offered by Hangfire's public APIs and retain outcome records for not-found or wrong-state attempts where applicable.

## Reading history

- `GET /hangfire/api/recurring/audit?limit=&jobId=` returns newest-first history.
- `GET /hangfire/api/runs/{id}/audit?limit=` returns history for one background job.
- Both embedded UIs present the relevant activity and per-job history.

The default read limit is controlled by `AuditDefaultReadLimit`. Actor extraction defaults to `HttpContext.User.Identity?.Name ?? "unknown"`; configure `ActorProvider` when the host uses another claim such as email.

The toolkit does not intercept operations performed through Hangfire's built-in dashboard or direct calls to Hangfire APIs. Use the toolkit surfaces when an audited operator path is required.
