using System.Security.Cryptography;
using System.Text;

namespace WeChatVoice.Core.Models;

public sealed record VoiceSelectionPlan(
    string WorkspaceId,
    string DataSetId,
    string AccountId,
    string ContactId,
    string ContactUsername,
    VoiceDirection Direction,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int? MaximumResults,
    string PlanFingerprint,
    VoiceScanReport ScanReport,
    long? MinimumDurationMs = null,
    long? MaximumDurationMs = null,
    long? MinimumPayloadBytes = null,
    long? MaximumPayloadBytes = null,
    bool ResolveDurations = false)
{
    public string QueryFingerprint => PlanFingerprint;

    public string ResultSetFingerprint => ScanReport.ResultSetFingerprint;

    public int ResultCount => ScanReport.MatchedVoiceCount;

    public long TotalPayloadBytes => ScanReport.TotalPayloadBytes;

    public static string ComputeFingerprint(string workspaceId, string dataSetId, string accountId,
        string contactId, string contactUsername, VoiceDirection direction,
        DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int? maximumResults,
        long? minimumDurationMs = null, long? maximumDurationMs = null,
        long? minimumPayloadBytes = null, long? maximumPayloadBytes = null,
        bool resolveDurations = false)
    {
        var value = string.Join("\n", workspaceId, dataSetId, accountId, contactId, contactUsername,
            direction, fromUtc?.ToUniversalTime().ToString("O") ?? "", toUtc?.ToUniversalTime().ToString("O") ?? "",
            maximumResults?.ToString() ?? "", minimumDurationMs?.ToString() ?? "",
            maximumDurationMs?.ToString() ?? "", minimumPayloadBytes?.ToString() ?? "",
            maximumPayloadBytes?.ToString() ?? "", resolveDurations ? "1" : "0");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
