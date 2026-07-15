using OpsToolkit.Hangfire.JobControl;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace OpsToolkit.Hangfire.Host.IntegrationTests;

// MapJobControlUi never touches JobStorage - it's a pure embedded-resource + placeholder-replacement
// endpoint - so a custom DashboardPath is verified against a bare TestServer host rather than the real
// Postgres-backed sample host the other test classes need. Deliberately not in the "Sample host"
// collection: no Hangfire storage here means no race with those hosts' startup.
public class JobControlUiDashboardPathTests
{
    [Fact]
    public async Task RecurringUi_CustomDashboardPath_ReplacesPlaceholder_AndTrimsTrailingSlash_Test()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddRouting());
                web.Configure(app => app.UseRouting().UseEndpoints(
                    endpoints => endpoints.MapJobControlUi(dashboardPath: "/admin/jobs/")));
            })
            .StartAsync();

        var html = await host.GetTestClient().GetStringAsync(JobControlEndpoints.DefaultUiPath);

        html.ShouldContain("var DASHBOARD_PATH = \"/admin/jobs\";");
        html.ShouldNotContain("{{DASHBOARD_PATH}}");
    }
}
