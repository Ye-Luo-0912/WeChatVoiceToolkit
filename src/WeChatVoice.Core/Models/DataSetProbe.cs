using System.Collections.ObjectModel;

namespace WeChatVoice.Core.Models;

public sealed record DataSetProbe
{
    public DataSetProbe(
        WeChatDataSet DataSet,
        DateTimeOffset ProbedAtUtc,
        IReadOnlyList<DataSetIssue>? Issues = null,
        IReadOnlyList<AdapterCandidate>? AdapterCandidates = null,
        string? SourceRoot = null,
        bool IncludesLocalPaths = false)
    {
        ArgumentNullException.ThrowIfNull(DataSet);
        this.DataSet = DataSet;
        this.ProbedAtUtc = ProbedAtUtc.ToUniversalTime();
        this.Issues = Freeze(Issues);
        this.AdapterCandidates = Freeze(AdapterCandidates);
        this.SourceRoot = SourceRoot;
        this.IncludesLocalPaths = IncludesLocalPaths;
    }

    public WeChatDataSet DataSet { get; }
    public DateTimeOffset ProbedAtUtc { get; }
    public IReadOnlyList<DataSetIssue> Issues { get; }
    public IReadOnlyList<AdapterCandidate> AdapterCandidates { get; }
    public string? SourceRoot { get; }
    public bool IncludesLocalPaths { get; }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? values)
        => new ReadOnlyCollection<T>((values ?? Array.Empty<T>()).ToArray());
}

public sealed record DataSetIssue(string Code, string Severity, string Message, string? RelativePath = null);

public sealed record AdapterCandidate(string AdapterId, bool IsMatch, int Score, string? Reason);

public sealed record DataSetProbeOptions(bool IncludeLocalPaths = false);
