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
    VoiceScanReport ScanReport)
{
    public string QueryFingerprint => PlanFingerprint;

    public string ResultSetFingerprint => ScanReport.ResultSetFingerprint;

    public int ResultCount => ScanReport.MatchedVoiceCount;

    public long TotalPayloadBytes => ScanReport.TotalPayloadBytes;

    public static string ComputeFingerprint(string workspaceId, string dataSetId, string accountId,
        string contactId, string contactUsername, VoiceDirection direction,
        DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int? maximumResults)
    {
        var value = string.Join("\n", workspaceId, dataSetId, accountId, contactId, contactUsername,
            direction, fromUtc?.ToUniversalTime().ToString("O") ?? "", toUtc?.ToUniversalTime().ToString("O") ?? "",
            maximumResults?.ToString() ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
