using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.States;
using Hangfire.Storage;
using Shouldly;
using Xunit;

namespace Hangfire.OpsToolkit.JobControl.Tests;

// Exercises CancellationRequestStore's marker read/write/clear against a real Postgres-backed
// JobStorage — the "empty string reads as absent" contract is a storage-layer behavior (SetJobParameter
// with "" vs a parameter that was never set), not something worth faking. Job parameters live on
// Hangfire.PostgreSql's jobparameter table keyed by the job's real (numeric) id — verified empirically
// that GetJobParameter/SetJobParameter throw FormatException for an id that was never a real job (same
// Convert.ToInt64 quirk noted on RunEndpoints' GET /{id} handler) — so, unlike AuditStoreTests and
// RecurringJobDisableStoreTests (which key off arbitrary string ids into hash/list structures with no
// such constraint), these tests need an actual created job.
public class CancellationRequestStoreTests
{
    private static JobStorage buildStorage()
        => new PostgreSqlStorage(buildConnectionString(), new PostgreSqlStorageOptions { PrepareSchemaIfNecessary = true });

    private static string buildConnectionString()
    {
        string env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;
        var host = env("HANGFIRE_OPSTOOLKIT_TEST_PG_HOST", "localhost");
        var port = env("HANGFIRE_OPSTOOLKIT_TEST_PG_PORT", "5434");
        var database = env("HANGFIRE_OPSTOOLKIT_TEST_PG_DATABASE", "hangfire_opstoolkit");
        var username = env("HANGFIRE_OPSTOOLKIT_TEST_PG_USERNAME", "postgres");
        var password = env("HANGFIRE_OPSTOOLKIT_TEST_PG_PASSWORD", "postgres");
        return $"host={host};port={port};database={database};username={username};password={password}";
    }

    private static string createJob(JobStorage storage)
        => new BackgroundJobClient(storage).Create(Job.FromExpression(() => TestJob.Run()), new EnqueuedState());

    [Fact]
    public void CancelRequest_RoundTrip_Test()
    {
        var storage = buildStorage();
        var jobId = createJob(storage);
        var at = new DateTime(2026, 7, 12, 3, 4, 5, DateTimeKind.Utc);

        using var connection = storage.GetConnection();

        CancellationRequestStore.Read(connection, jobId).ShouldBeNull();

        CancellationRequestStore.Write(connection, jobId, "tester", at, "integration test");
        var marker = CancellationRequestStore.Read(connection, jobId);
        marker.ShouldNotBeNull();
        marker!.By.ShouldBe("tester");
        marker.At.ShouldBe(at);
        marker.Reason.ShouldBe("integration test");

        CancellationRequestStore.Clear(connection, jobId);
        CancellationRequestStore.Read(connection, jobId).ShouldBeNull();
    }

    public static class TestJob
    {
        public static void Run()
        {
        }
    }
}
