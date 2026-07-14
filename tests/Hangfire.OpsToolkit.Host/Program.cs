using Hangfire;
using Hangfire.Dashboard;
using Hangfire.OpsToolkit.JobControl;
using Hangfire.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Hangfire")
    ?? "host=localhost;port=5434;database=hangfire_opstoolkit;username=postgres;password=postgres";

builder.Services.AddAuthentication();
builder.Services.AddAuthorization(options =>
{
    // Demo-only: anyone can view and manage. A real host wires these to its own identity/roles —
    // MapJobControl takes policy names precisely so this swap is the only thing that changes.
    options.AddPolicy(Policies.View, policy => policy.RequireAssertion(_ => true));
    options.AddPolicy(Policies.Manage, policy => policy.RequireAssertion(_ => true));
});

builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString))
    .UseJobControl());

builder.Services.AddHangfireServer(options =>
{
    // Tuned down from the 5s default so the cancel-protocol integration/manual tests (which wait for the
    // watcher to observe an abort) run in a
    // reasonable time. A production host should weigh this against the per-tick GetStateData cost per
    // watched token before copying this value.
    options.CancellationCheckInterval = TimeSpan.FromSeconds(1);
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// MapHangfireDashboard (endpoint routing), not UseHangfireDashboard (a Map()-branch middleware that
// would exclusively own the whole /hangfire/* prefix and swallow MapJobControl's routes below it).
app.MapHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new AllowAllDashboardAuthorizationFilter() },
});

app.MapJobControl(
    viewPolicy: Policies.View,
    managePolicy: Policies.Manage);

// A few demo recurring jobs so there's something to see/disable/enable/trigger from
// /hangfire/job-control/recurring (or the built-in /hangfire dashboard) right after `dotnet run`.
RecurringJob.AddOrUpdate<DemoJobs>("heartbeat-every-minute", job => job.Heartbeat(), Cron.Minutely());
RecurringJob.AddOrUpdate<DemoJobs>("nightly-report", job => job.NightlyReport(), Cron.Daily());
RecurringJob.AddOrUpdate<DemoJobs>("flaky-every-2-minutes", job => job.SometimesFails(), "*/2 * * * *");

// Never fire on their own (Cron.Never()) — seeded purely so "Trigger now" on the Recurring page can put
// one in the Processing tab on demand for local verification.
RecurringJob.AddOrUpdate<CancellationTestJobs>("cancel-demo-token-honoring", job => job.HonoringLoop(default), Cron.Never());
RecurringJob.AddOrUpdate<CancellationTestJobs>("cancel-demo-token-ignoring", job => job.IgnoringLoop(), Cron.Never());

app.Run();

// Exposed for Microsoft.AspNetCore.Mvc.Testing's WebApplicationFactory<Program> in the integration
// test project.
public partial class Program { }

internal static class Policies
{
    public const string View = "OpsToolkit.View";
    public const string Manage = "OpsToolkit.Manage";
}

internal sealed class AllowAllDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => true;
}

public class DemoJobs
{
    public void Heartbeat() => Console.WriteLine($"[{DateTime.UtcNow:O}] heartbeat");

    public void NightlyReport() => Console.WriteLine($"[{DateTime.UtcNow:O}] nightly report generated");

    // Fails ~1 in 3 runs — gives you something to disable from the job-control UI and watch stop
    // paging, versus a real failure you'd want to keep retrying.
    public void SometimesFails()
    {
        if (Random.Shared.Next(3) == 0)
            throw new InvalidOperationException(
                "Simulated transient failure — try disabling this job from /hangfire/job-control.");
        Console.WriteLine($"[{DateTime.UtcNow:O}] flaky job ran fine this time");
    }
}

// Fixtures for the cancel protocol — one job that observes an
// abort promptly (flows its token into awaited work, mechanic #3) and one that can't (no token at all,
// so it runs to completion regardless of any cancel request — the "completed anyway" case, §2.3). The
// cancel-protocol integration tests enqueue these directly (BackgroundJob.Enqueue); the demo host also
// registers them as never-firing recurring jobs so "Trigger now" can seed one on demand for manual
// verification against the Job Runs UI.
public class CancellationTestJobs
{
    public async Task HonoringLoop(CancellationToken token)
    {
        for (var i = 0; i < 3000; i++) // up to ~60s at the 200ms step below — tests cancel it well before that
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), token);
        }
    }

    public void IgnoringLoop()
    {
        Thread.Sleep(TimeSpan.FromSeconds(4)); // long enough for a test to cancel it mid-flight
    }

    // Deterministic Failed fixture for the requeue/delete tests (RequeueDeleteApiTests) — no automatic
    // retry, so it lands in Failed on the very first attempt instead of cycling through Scheduled
    // backoff first.
    [AutomaticRetry(Attempts = 0)]
    public void AlwaysFails() => throw new InvalidOperationException("Seeded failure for requeue/delete tests.");
}
