using System.Text.Json;
using Hangfire.Server;
using Hangfire.Storage;

namespace OpsToolkit.Hangfire.JobControl;

/// <summary>One recorded cancel request — see <see cref="CancellationRequestStore"/> for the storage contract.</summary>
public sealed record CancelRequestMarker(int V, string By, DateTime At, string Reason)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// Reads and writes the correlation marker used by the processing-job cancellation protocol — a
/// single job parameter, <c>JobControl.CancelRequested</c>, on the target background job. Job parameters
/// are core-interface storage (mechanic #12): they live with the job and expire with it, so this needs
/// no cleanup process of its own. Deliberately bypasses <c>PerformContext.SetJobParameter&lt;T&gt;</c>/
/// <c>GetJobParameter&lt;T&gt;</c> (the generic convenience wrappers on <see cref="PerformContext"/>)
/// in favor of the core <see cref="IStorageConnection.SetJobParameter"/>/<see cref="IStorageConnection.GetJobParameter"/>
/// members directly — those wrappers additionally JSON-serialize whatever value is handed to them, which
/// would double-encode a value that is already our own JSON shape. Same reasoning as
/// <see cref="AuditEntry"/>'s bespoke (de)serialization.
/// </summary>
public static class CancellationRequestStore
{
    private const string ParameterName = "JobControl.CancelRequested";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Writes the marker. Only meaningful when the cancelled job was <c>Processing</c> (§2.1 step 2) —
    /// a queued/scheduled cancel has no running body to acknowledge, so callers must not call this for
    /// those cases.
    /// </summary>
    public static void Write(IStorageConnection connection, string jobId, string by, DateTime at, string reason)
        => connection.SetJobParameter(jobId, ParameterName, JsonSerializer.Serialize(new CancelRequestMarker(CancelRequestMarker.CurrentVersion, by, at, reason), JsonOptions));

    /// <summary>Null when absent, cleared, or unparsable — a corrupt/foreign parameter value must not throw.</summary>
    public static CancelRequestMarker? Read(IStorageConnection connection, string jobId)
    {
        var raw = connection.GetJobParameter(jobId, ParameterName);
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            return JsonSerializer.Deserialize<CancelRequestMarker>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Overwrites the marker with an empty string — the core <c>IStorageConnection</c> interface has no
    /// parameter-delete member, and an empty value reads back as absent (<see cref="Read"/>). Called on
    /// requeue so a cancelled-then-requeued job's next run doesn't inherit a stale marker and
    /// record a phantom <c>completed-anyway</c> ack.
    /// </summary>
    public static void Clear(IStorageConnection connection, string jobId)
        => connection.SetJobParameter(jobId, ParameterName, "");
}
