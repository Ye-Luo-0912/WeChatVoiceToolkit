using System.Security.Cryptography;
using System.Text;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;

namespace WeChatVoice.Workflows.Tests;

/// <summary>
/// In-process fake data-set backend: a fake adapter and catalog with a canned
/// set of voice records. It lets workflow tests cover orchestration (contact
/// resolution, scan state counts, export commit, cancellation, retry) without
/// any real Weixin process, SQLite, or process-memory access.
/// </summary>
public sealed class FakeBackend
{
    public const string AccountId = "wxid_owner";
    public const string ContactUsername = "wxid_peer";

    private readonly List<VoiceRecord> _voices;
    private readonly Func<CancellationToken, IAsyncEnumerable<VoiceRecord>>? _voicesFactory;

    public FakeBackend(
        IEnumerable<VoiceRecord>? voices = null,
        Func<CancellationToken, IAsyncEnumerable<VoiceRecord>>? voicesFactory = null,
        Action<CancellationToken>? onQueryCancellation = null)
    {
        _voices = voices?.ToList() ?? [];
        _voicesFactory = voicesFactory;
        OnQueryCancellation = onQueryCancellation;
    }

    public Action<CancellationToken>? OnQueryCancellation { get; set; }

    public int OpenPayloadCount { get; private set; }

    public VoiceQuery? LastVoiceQuery { get; private set; }

    /// <summary>Replaces the canned voice set (used to make failures retryable).</summary>
    public void Fill(params VoiceRecord[] records)
    {
        _voices.Clear();
        _voices.AddRange(records);
    }

    public byte[] LinkedPayload { get; set; } = Encoding.ASCII.GetBytes("#!SILK_V3\n");

    public IWeChatDataSetAdapter Adapter => new FakeAdapter(this);

    public IAsyncEnumerable<ContactRecord> QueryContactsAsync(ContactQuery query, CancellationToken cancellationToken)
    {
        var records = new List<ContactRecord>
        {
            new ContactRecord(ContactUsername, ContactUsername, "Peer Remark", "Peer Remark", "peer_alias", ConversationId: ContactUsername),
        };
        if (query.Username is not null)
        {
            records = records.Where(record => string.Equals(record.Username, query.Username, StringComparison.Ordinal)).ToList();
        }

        if (query.SearchTerm is not null)
        {
            records = records.Where(record =>
                (record.Username ?? string.Empty).Contains(query.SearchTerm, StringComparison.OrdinalIgnoreCase)
                || (record.DisplayName ?? string.Empty).Contains(query.SearchTerm, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return ToAsync(records, cancellationToken);
    }

    public IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(VoiceQuery query, CancellationToken cancellationToken)
    {
        LastVoiceQuery = query;
        OnQueryCancellation?.Invoke(cancellationToken);
        if (_voicesFactory is not null)
        {
            return _voicesFactory(cancellationToken);
        }

        var matching = _voices.Where(record =>
            string.Equals(record.ConversationId, query.ConversationId, StringComparison.Ordinal)
            && (query.Direction is null || record.Direction == query.Direction)).ToList();
        if (query.MaximumResults is not null)
        {
            matching = matching.Take(query.MaximumResults.Value).ToList();
        }

        return ToAsync(matching, cancellationToken);
    }

    public ValueTask<Stream> OpenPayloadAsync(VoicePayloadLocator locator, CancellationToken cancellationToken)
    {
        OpenPayloadCount++;
        return ValueTask.FromResult<Stream>(new MemoryStream(LinkedPayload, writable: false));
    }

    private sealed class FakeAdapter(FakeBackend backend) : IWeChatDataSetAdapter
    {
        public string Id => "fake-adapter";

        public AdapterMatch Probe(WeChatDataSet dataSet) => AdapterMatch.Match(100, "fake");

        public ValueTask<IVoiceCatalog> OpenAsync(VerifiedLocalWorkspace workspace, CancellationToken cancellationToken)
            => ValueTask.FromResult<IVoiceCatalog>(new FakeCatalog(backend));
    }

    private sealed class FakeCatalog(FakeBackend backend) : IVoiceCatalog
    {
        public VoiceCatalogContext Context { get; } = new(
            "dataset-fake",
            "fake-adapter",
            "fake-v1",
            AccountId,
            ["fingerprint-1"],
            "snapshot-fake",
            "fake-adapter",
            null,
            new AccountIdentity(AccountIdentityState.Confirmed, "fake"));

        public IAsyncEnumerable<ContactRecord> QueryContactsAsync(ContactQuery query, CancellationToken cancellationToken)
            => backend.QueryContactsAsync(query, cancellationToken);

        public IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(VoiceQuery query, CancellationToken cancellationToken)
            => backend.QueryVoicesAsync(query, cancellationToken);

        public ValueTask<Stream> OpenPayloadAsync(VoicePayloadLocator locator, CancellationToken cancellationToken)
            => backend.OpenPayloadAsync(locator, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> values, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }

    /// <summary>Builds a linked voice record with a stable locator.</summary>
    public static VoiceRecord Linked(string id, long occurredUnix, VoiceDirection direction, long length = 10, string? sha256 = null)
        => new VoiceRecord(
            id,
            ContactUsername,
            DateTimeOffset.FromUnixTimeSeconds(occurredUnix),
            direction,
            new VoicePayloadLocator("media", 0, id),
            SourceDatabase: "media_0.db",
            ShardNumber: 0,
            SnapshotId: "snapshot-fake",
            AdapterId: "fake-adapter",
            AccountId: AccountId,
            PayloadSha256: sha256 ?? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("#!SILK_V3\n"))).ToLowerInvariant(),
            PayloadByteLength: length,
            DurationMs: length * 20,
            MediaLinked: true,
            SpeakerId: direction == VoiceDirection.Incoming ? ContactUsername : AccountId,
            DataSetId: "dataset-fake",
            AdapterVersion: "fake-v1",
            DatabaseFingerprints: ["fingerprint-1"],
            AdapterFamily: "fake-adapter",
            AccountStableId: AccountId,
            ConversationStableId: ContactUsername,
            PayloadState: VoicePayloadState.Linked);

    /// <summary>Builds a non-exportable record (Missing / Empty / InvalidHeader / Ambiguous).</summary>
    public static VoiceRecord Broken(string id, long occurredUnix, VoiceDirection direction, VoicePayloadState state)
        => new VoiceRecord(
            id,
            ContactUsername,
            DateTimeOffset.FromUnixTimeSeconds(occurredUnix),
            direction,
            PayloadLocator: null,
            SourceDatabase: "media_0.db",
            ShardNumber: 0,
            MediaLinked: false,
            SpeakerId: ContactUsername,
            AccountId: AccountId,
            PayloadState: state);
}
