using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Hangfire.OpsToolkit.Host.IntegrationTests;

// Drives RunEndpoints' requeue/delete mutations against
// the real sample host — real Postgres storage, a real AddHangfireServer(). CancellationTestJobs.AlwaysFails
// (Program.cs, [AutomaticRetry(Attempts = 0)]) is the fixture for a deterministic Failed job; DemoJobs.Heartbeat
// gives a deterministic Succeeded one. [Collection("Sample host")]: see SampleHostCollection.
[Collection("Sample host")]
public class RequeueDeleteApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    private readonly HttpClient _client;

    public RequeueDeleteApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Requeue_Failed_RerunsSameJobId_Test()
    {
        var jobId = BackgroundJob.Enqueue<CancellationTestJobs>(x => x.AlwaysFails());
        await waitForState(jobId, "Failed");

        (await requeue(jobId, "retry after fix", "Failed")).EnsureSuccessStatusCode();

        var entry = (await getRunsAudit(jobId)).First(e => e.Action == "requeue");
        entry.Outcome.ShouldBe("ok");
        entry.Detail!["FromState"].ShouldBe("Failed");

        // AlwaysFails always throws, so the same background job id accumulates a second Failed entry in
        // its state history rather than a new id being created.
        var details = await waitForFailedHistoryCount(jobId, minCount: 2);
        details.Id.ShouldBe(jobId);
        details.History.Count(h => h.StateName == "Failed").ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Requeue_Processing_Rejected_Test()
    {
        var jobId = BackgroundJob.Enqueue<CancellationTestJobs>(x => x.HonoringLoop(default));
        await waitForState(jobId, "Processing");

        var response = await requeue(jobId, reason: null, expectedState: "Processing");
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var entry = (await getRunsAudit(jobId)).First(e => e.Action == "requeue");
        entry.Outcome.ShouldNotBe("ok");

        // Clean up rather than leave a 60s loop running for the rest of the suite.
        await cancel(jobId, "test cleanup", "Processing");
    }

    [Fact]
    public async Task Requeue_ClearsCancelMarker_Test()
    {
        var jobId = BackgroundJob.Enqueue<CancellationTestJobs>(x => x.HonoringLoop(default));
        await waitForState(jobId, "Processing");
        (await cancel(jobId, "integration test", "Processing")).EnsureSuccessStatusCode();
        await waitForState(jobId, "Deleted");
        await waitForAuditAction(jobId, "cancel-ack"); // marker exists and is acknowledged before requeue

        getMarker(jobId).ShouldNotBeNull();

        (await requeue(jobId, reason: null, expectedState: "Deleted")).EnsureSuccessStatusCode();

        // §2.3: requeue clears the marker so the requeued run's own ordinary completion can't record a
        // phantom completed-anyway ack from this stale request.
        getMarker(jobId).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_Succeeded_SnapshotsDetail_Test()
    {
        var jobId = BackgroundJob.Enqueue<DemoJobs>(x => x.Heartbeat());
        await waitForState(jobId, "Succeeded");

        (await deleteRun(jobId, "cleanup", "Succeeded")).EnsureSuccessStatusCode();

        await waitForState(jobId, "Deleted");

        var entry = (await getRunsAudit(jobId)).First(e => e.Action == "delete-run");
        entry.Outcome.ShouldBe("ok");
        entry.Detail!["FromState"].ShouldBe("Succeeded");
        entry.Detail.ShouldContainKey("JobDisplayName");
    }

    // §5's actual payoff, against a REAL triggered recurring job rather than synthetic Detail entries
    // (AuditStoreTests' AuditRead_JobIdFilter_MatchesCorrelationIds_Test covers the filter logic in
    // isolation). Hangfire stamps its own RecurringJobId parameter JSON-encoded (a plain string value
    // going through CreateContext.Parameters lands as the JSON literal "heartbeat-every-minute", quotes
    // included — see AuditStore.TryGetRecurringJobId's remarks); a raw, undecoded read here would never
    // equal the bare id a caller queries by, so this pins that the decode actually happens, not just
    // that the matching logic would work if it did.
    [Fact]
    public async Task Delete_Audit_VisibleViaRecurringPageAudit_Test()
    {
        const string recurringJobId = "heartbeat-every-minute";

        (await _client.PostAsync($"/hangfire/api/recurring/{recurringJobId}/trigger", content: null)).EnsureSuccessStatusCode();
        var triggerEntry = (await getRecurringAudit(recurringJobId)).First(e => e.Action == "trigger");
        var backgroundJobId = triggerEntry.Detail!["BackgroundJobId"];

        await waitForState(backgroundJobId, "Succeeded");
        (await deleteRun(backgroundJobId, "cleanup", "Succeeded")).EnsureSuccessStatusCode();

        var deleteEntry = (await getRunsAudit(backgroundJobId)).First(e => e.Action == "delete-run");
        deleteEntry.Detail!["RecurringJobId"].ShouldBe(recurringJobId);

        // The Recurring page's own /audit?jobId= — no correlation extension on that endpoint itself,
        // just the shared AuditStore.Read matching Detail["RecurringJobId"] — finds the run-level entry
        // with zero UI changes.
        (await getRecurringAudit(recurringJobId)).ShouldContain(e => e.Action == "delete-run" && e.JobId == backgroundJobId);
    }

    private Task<HttpResponseMessage> requeue(string jobId, string? reason, string? expectedState)
        => _client.PostAsJsonAsync($"/hangfire/api/runs/{Uri.EscapeDataString(jobId)}/requeue", new { reason, expectedState });

    private Task<HttpResponseMessage> deleteRun(string jobId, string? reason, string? expectedState)
        => _client.PostAsJsonAsync($"/hangfire/api/runs/{Uri.EscapeDataString(jobId)}/delete", new { reason, expectedState });

    private Task<HttpResponseMessage> cancel(string jobId, string? reason, string? expectedState)
        => _client.PostAsJsonAsync($"/hangfire/api/runs/{Uri.EscapeDataString(jobId)}/cancel", new { reason, expectedState });

    private static string? getMarker(string jobId)
    {
        using var connection = JobStorage.Current.GetConnection();
        return connection.GetJobParameter(jobId, "JobControl.CancelRequested") is { Length: > 0 } raw ? raw : null;
    }

    private static string? getJobState(string jobId)
    {
        using var connection = JobStorage.Current.GetConnection();
        return connection.GetJobData(jobId)?.State;
    }

    private static async Task<string?> waitForState(string jobId, string state)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        string? current;
        do
        {
            current = getJobState(jobId);
            if (string.Equals(current, state, StringComparison.OrdinalIgnoreCase)) return current;
            await Task.Delay(PollInterval);
        } while (DateTime.UtcNow < deadline);
        return current;
    }

    private async Task<AuditEntryDto?> waitForAuditAction(string jobId, string action)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        AuditEntryDto? match;
        do
        {
            match = (await getRunsAudit(jobId)).FirstOrDefault(e => e.Action == action);
            if (match != null) return match;
            await Task.Delay(PollInterval);
        } while (DateTime.UtcNow < deadline);
        return match;
    }

    private async Task<JobDetailsResponseDto> waitForFailedHistoryCount(string jobId, int minCount)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        JobDetailsResponseDto details;
        do
        {
            details = await getJobDetails(jobId);
            if (details.History.Count(h => h.StateName == "Failed") >= minCount) return details;
            await Task.Delay(PollInterval);
        } while (DateTime.UtcNow < deadline);
        return details;
    }

    private async Task<List<AuditEntryDto>> getRunsAudit(string jobId)
    {
        var response = await _client.GetAsync($"/hangfire/api/runs/{Uri.EscapeDataString(jobId)}/audit");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<AuditEntryDto>>(JsonOptions))!;
    }

    private async Task<List<AuditEntryDto>> getRecurringAudit(string jobId)
    {
        var response = await _client.GetAsync($"/hangfire/api/recurring/audit?jobId={Uri.EscapeDataString(jobId)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<AuditEntryDto>>(JsonOptions))!;
    }

    private async Task<JobDetailsResponseDto> getJobDetails(string id)
    {
        var response = await _client.GetAsync($"/hangfire/api/runs/{Uri.EscapeDataString(id)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobDetailsResponseDto>(JsonOptions))!;
    }

    // Local mirrors of RunEndpoints'/JobControlEndpoints' wire records — same reasoning as the other test
    // classes' local mirrors: these tests only depend on the wire shape, the way an external consumer would.
    private sealed record JobDetailsResponseDto(
        string Id, string? JobDisplayName, Dictionary<string, string>? Properties,
        DateTime? CreatedAt, DateTime? ExpireAt, List<HistoryEntryDto> History);

    private sealed record HistoryEntryDto(string StateName, string? Reason, DateTime CreatedAt, Dictionary<string, string>? Data);

    private sealed record AuditEntryDto(
        int V, DateTime At, string Actor, string Action, string JobId, string? Reason, string Outcome, Dictionary<string, string>? Detail);
}
