using Xunit;

namespace OpsToolkit.Hangfire.Host.IntegrationTests;

/// <summary>
/// Both <see cref="JobControlApiTests"/> and <see cref="RunEndpointsApiTests"/> each spin up their own
/// <c>WebApplicationFactory&lt;Program&gt;</c> — a real Kestrel test server plus a real
/// <c>AddHangfireServer()</c> — against the <b>same</b> local Postgres. xUnit parallelizes across test
/// classes by default; two of these hosts starting at the same instant have been observed to race
/// inside ASP.NET Core's own hosting startup (<c>ApplicationLifetime.NotifyStarted()</c> firing after a
/// host's <c>LoggerFactory</c> was already disposed elsewhere in the process — unrelated to anything
/// either test class does). Putting both classes in this collection makes xUnit run them sequentially
/// instead, which is enough to avoid the race; each class still gets its own fixture instance.
/// </summary>
[CollectionDefinition("Sample host", DisableParallelization = true)]
public class SampleHostCollection { }
