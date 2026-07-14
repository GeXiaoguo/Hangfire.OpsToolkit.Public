using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Hangfire.OpsToolkit.Host.IntegrationTests;

// Drives the Job Runs read-plane HTTP API (RunEndpoints — PR1 of the in-flight-cancellation plan: read
// facade + naming, no mutations yet) against the real sample host. Complements JobControlApiTests,
// which covers the Recurring Jobs page's own API.
// [Collection("Sample host")]: see SampleHostCollection — this class and JobControlApiTests each boot a
// full WebApplicationFactory<Program> against the same Postgres and must not start concurrently.
[Collection("Sample host")]
public class RunEndpointsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public RunEndpointsApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/hangfire/api/runs/stats")]
    [InlineData("/hangfire/api/runs/queues")]
    [InlineData("/hangfire/api/runs/enqueued")]
    [InlineData("/hangfire/api/runs/processing")]
    [InlineData("/hangfire/api/runs/scheduled")]
    [InlineData("/hangfire/api/runs/succeeded")]
    [InlineData("/hangfire/api/runs/failed")]
    [InlineData("/hangfire/api/runs/deleted")]
    [InlineData("/hangfire/api/runs/servers")]
    public async Task ReadEndpoint_ReturnsOk_Test(string path)
    {
        (await _client.GetAsync(path)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Stats_ReflectsRunningServer_Test()
    {
        var stats = await getStats();
        stats.Servers.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task JobDetails_UnknownId_ReturnsNotFound_Test()
    {
        var response = await _client.GetAsync($"/hangfire/api/runs/does-not-exist-{Guid.NewGuid()}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task JobDetails_TriggeredRecurringJob_ReturnsHistory_Test()
    {
        // Trigger a known recurring job to get a real background-job id, the same correlation seed
        // JobControlApiTests.Trigger_Audit_CarriesBackgroundJobId_Test relies on for the audit side.
        (await _client.PostAsync("/hangfire/api/recurring/heartbeat-every-minute/trigger", content: null))
            .EnsureSuccessStatusCode();

        var auditResponse = await _client.GetAsync("/hangfire/api/recurring/audit?limit=1&jobId=heartbeat-every-minute");
        auditResponse.EnsureSuccessStatusCode();
        var audit = await auditResponse.Content.ReadFromJsonAsync<List<AuditEntryDto>>(JsonOptions);
        var backgroundJobId = audit!.Single().Detail!["BackgroundJobId"];

        var details = await getJobDetails(backgroundJobId);
        details.Id.ShouldBe(backgroundJobId);
        details.History.ShouldNotBeEmpty();
        details.History.ShouldContain(h => h.StateName == "Enqueued");
    }

    [Fact]
    public async Task Enqueued_DefaultsToFirstQueue_WhenQueueNotSpecified_Test()
    {
        // No ?queue= — RunEndpoints.MapJobRunsApi falls back to the first queue from /queues rather
        // than erroring, so the tab isn't stuck empty on first load before a picker selection is made.
        (await _client.GetAsync("/hangfire/api/runs/enqueued")).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999_999)]
    public async Task Succeeded_ClampsCount_WithoutErroring_Test(int requestedCount)
    {
        (await _client.GetAsync($"/hangfire/api/runs/succeeded?count={requestedCount}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task LegacyUiPath_RedirectsToRecurringUi_Test()
    {
        var noRedirectClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await noRedirectClient.GetAsync("/hangfire/job-control");
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/hangfire/job-control/recurring");
    }

    [Fact]
    public async Task RecurringUi_ServesHtml_WithCrossNavToRuns_Test()
    {
        var html = await (await _client.GetAsync("/hangfire/job-control/recurring")).Content.ReadAsStringAsync();
        html.ShouldContain("href=\"/hangfire/job-control/runs\"");
    }

    [Fact]
    public async Task RunsUi_ServesHtml_WithCrossNavToRecurring_Test()
    {
        var html = await (await _client.GetAsync("/hangfire/job-control/runs")).Content.ReadAsStringAsync();
        html.ShouldContain("href=\"/hangfire/job-control/recurring\"");
    }

    [Fact]
    public async Task RecurringUi_ServesHtml_WithDefaultDashboardPath_Test()
    {
        // Program.cs's MapJobControl call passes no JobControlOptions, so this exercises the
        // JobControlOptions.DashboardPath default ("/hangfire") flowing all the way through
        // MapJobControl -> MapJobControlUi's {{DASHBOARD_PATH}} substitution.
        var html = await (await _client.GetAsync("/hangfire/job-control/recurring")).Content.ReadAsStringAsync();
        html.ShouldContain("var DASHBOARD_PATH = \"/hangfire\";");
        html.ShouldNotContain("{{DASHBOARD_PATH}}");
    }

    private async Task<StatsDto> getStats()
    {
        var response = await _client.GetAsync("/hangfire/api/runs/stats");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StatsDto>(JsonOptions))!;
    }

    private async Task<JobDetailsResponseDto> getJobDetails(string id)
    {
        var response = await _client.GetAsync($"/hangfire/api/runs/{Uri.EscapeDataString(id)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobDetailsResponseDto>(JsonOptions))!;
    }

    // Local mirrors of RunEndpoints' wire records — kept separate from the library's own types so these
    // tests only depend on the wire shape, the same way an external API consumer would.
    private sealed record StatsDto(long Enqueued, long Scheduled, long Processing, long Succeeded, long Failed, long Deleted, long Servers);

    private sealed record JobDetailsResponseDto(
        string Id, string? JobDisplayName, Dictionary<string, string>? Properties,
        DateTime? CreatedAt, DateTime? ExpireAt, List<HistoryEntryDto> History);

    private sealed record HistoryEntryDto(string StateName, string? Reason, DateTime CreatedAt, Dictionary<string, string>? Data);

    // Local mirror of JobControlEndpoints.AuditEntry — same reasoning as JobControlApiTests' own copy.
    private sealed record AuditEntryDto(
        int V, DateTime At, string Actor, string Action, string JobId, string? Reason, string Outcome, Dictionary<string, string>? Detail);
}
