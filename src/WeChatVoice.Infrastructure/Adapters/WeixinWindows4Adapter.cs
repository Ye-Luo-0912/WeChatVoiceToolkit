using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WeChatVoice.Core.Models;
using WeChatVoice.Core.Ports;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Infrastructure.Adapters;

/// <summary>
/// Exact adapter for the schema verified from Weixin Windows 4.1.11.55.
/// It deliberately refuses partial or merely similar schemas.
/// </summary>
public sealed class WeixinWindows4Adapter : IWeChatDataSetAdapter
{
    internal const string AdapterId = "weixin-windows-4";
    internal const string AdapterVersion = Weixin41155SchemaSignature.Id;
    private const string MaterializationBackendId = "sqlcipher-e_sqlcipher-worker";
    private const string KeyExtractionProfileId = "weixin-windows-4.1.11.55-wcdb-protected-spec-v2";
    private const string ProcessVersion = "4.1.11.55";
    private const string ProcessImageSha256 = "ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d";
    private const string WcdbModuleSha256 = "ab925b9428239def44b252d970c337034d75e66b27eb5529633dc10669fc796a";

    public string Id => AdapterId;

    public AdapterMatch Probe(WeChatDataSet dataSet)
    {
        ArgumentNullException.ThrowIfNull(dataSet);

        var contacts = dataSet.Databases.Where(IsContactArtifact).ToArray();
        if (contacts.Length != 1)
        {
            return AdapterMatch.NoMatch($"Expected exactly one verified contact database; found {contacts.Length}.");
        }

        var media = dataSet.Databases.Where(IsMediaArtifact).ToArray();
        if (media.Length != 1)
        {
            return AdapterMatch.NoMatch($"Expected exactly one verified media database for this schema version; found {media.Length}.");
        }

        var declaredMessages = dataSet.Databases
            .Where(static artifact => RoleEquals(artifact, "message"))
            .ToArray();
        if (declaredMessages.Length == 0 || declaredMessages.Any(static artifact => !IsMessageArtifact(artifact)))
        {
            return AdapterMatch.NoMatch("Every declared message database must contain Name2Id and at least one exact Msg_<md5(username)> table schema.");
        }

        return AdapterMatch.Match(100, "Verified Weixin Windows 4.1.11.55 contact/message/media schema mapping.");
    }

    public async ValueTask<IVoiceCatalog> OpenAsync(
        VerifiedLocalWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();
        var dataSet = workspace.DataSet;
        if (!Probe(dataSet).IsMatch)
        {
            throw new NoMatchingDataSetAdapterException(dataSet.DataSetId);
        }

        if (string.IsNullOrWhiteSpace(dataSet.AccountId))
        {
            throw new InvalidDataException("The verified local workspace does not contain a stable Weixin account ID.");
        }

        var provenance = workspace.Workspace.Provenance;
        if (provenance is null
            || !string.Equals(provenance.KeyExtractionProfileId, KeyExtractionProfileId, StringComparison.Ordinal)
            || !string.Equals(provenance.ProcessVersion, ProcessVersion, StringComparison.Ordinal)
            || !string.Equals(provenance.ProcessImageSha256, ProcessImageSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(provenance.WcdbModuleSha256, WcdbModuleSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(provenance.BackendId, MaterializationBackendId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The verified workspace lacks complete materialization provenance for the exact Weixin adapter.");
        }

        WindowsSqliteProvider.EnsureInitialized();
        var contact = dataSet.Databases.Single(IsContactArtifact);
        var media = dataSet.Databases.Single(IsMediaArtifact);
        var messages = dataSet.Databases
            .Where(static artifact => RoleEquals(artifact, "message"))
            .OrderBy(static artifact => artifact.ShardNumber)
            .ThenBy(static artifact => artifact.DatabasePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await ValidateAccountIdentityAsync(contact, messages, dataSet.AccountId, cancellationToken).ConfigureAwait(false);
        return new WeixinWindows4VoiceCatalog(workspace, contact, messages, media);
    }

    private static async Task ValidateAccountIdentityAsync(
        DatabaseArtifact contact,
        IReadOnlyList<DatabaseArtifact> messages,
        string accountId,
        CancellationToken cancellationToken)
    {
        await using (var connection = await OpenReadOnlyAsync(contact, cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM contact WHERE username = $username;";
            command.Parameters.AddWithValue("$username", accountId);
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (count != 1)
            {
                throw new InvalidDataException($"Workspace account identity did not resolve exactly once in the contact database (matches: {count}).");
            }
        }

        foreach (var message in messages)
        {
            await using var connection = await OpenReadOnlyAsync(message, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Name2Id WHERE user_name = $username;";
            command.Parameters.AddWithValue("$username", accountId);
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (count != 1)
            {
                throw new InvalidDataException($"Workspace account identity did not resolve exactly once in message shard '{message.DatabasePath}' (matches: {count}).");
            }
        }
    }

    internal static async Task<SqliteConnection> OpenReadOnlyAsync(DatabaseArtifact artifact, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifact.LocalPath))
        {
            throw new InvalidDataException($"Verified artifact '{artifact.DatabasePath}' lacks a local path.");
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = artifact.LocalPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsContactArtifact(DatabaseArtifact artifact)
        => RoleEquals(artifact, "contact") && Weixin41155SchemaSignature.MatchesContact(artifact.Schema);

    private static bool IsMediaArtifact(DatabaseArtifact artifact)
        => RoleEquals(artifact, "media")
            && Weixin41155SchemaSignature.MatchesMediaName(artifact.Schema)
            && Weixin41155SchemaSignature.MatchesMedia(artifact.Schema);

    private static bool IsMessageArtifact(DatabaseArtifact artifact)
        => RoleEquals(artifact, "message")
            && Weixin41155SchemaSignature.MatchesMessageName(artifact.Schema)
            && artifact.Schema.Objects.Any(schemaObject => IsMessageTable(schemaObject)
                && Weixin41155SchemaSignature.MatchesMessageTable(artifact.Schema, schemaObject.Name));

    internal static bool IsMessageTable(SchemaObjectSnapshot schemaObject)
    {
        const string prefix = "Msg_";
        if (schemaObject.Kind != SchemaObjectKind.Table
            || !schemaObject.Name.StartsWith(prefix, StringComparison.Ordinal)
            || schemaObject.Name.Length != prefix.Length + 32)
        {
            return false;
        }

        var normalized = Weixin41155SchemaSignature.NormalizeMessageTableName(schemaObject.Name);
        return normalized.Length == prefix.Length + 32
            && normalized.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    private static bool RoleEquals(DatabaseArtifact artifact, string role)
        => string.Equals(artifact.LogicalRole, role, StringComparison.OrdinalIgnoreCase);
}

internal sealed class WeixinWindows4VoiceCatalog : IVoiceCatalog
{
    private readonly VerifiedLocalWorkspace _workspace;
    private readonly DatabaseArtifact _contact;
    private readonly IReadOnlyList<DatabaseArtifact> _messages;
    private readonly DatabaseArtifact _media;
    private bool _disposed;

    internal WeixinWindows4VoiceCatalog(
        VerifiedLocalWorkspace workspace,
        DatabaseArtifact contact,
        IReadOnlyList<DatabaseArtifact> messages,
        DatabaseArtifact media)
    {
        _workspace = workspace;
        _contact = contact;
        _messages = messages;
        _media = media;
        var dataSet = workspace.DataSet;
        Context = new VoiceCatalogContext(
            dataSet.DataSetId,
            WeixinWindows4Adapter.AdapterId,
            WeixinWindows4Adapter.AdapterVersion,
            dataSet.AccountId,
            dataSet.Databases.Select(static artifact => artifact.DatabaseGroupFingerprint ?? artifact.MainSha256).ToArray(),
            workspace.Workspace.Provenance?.SourceSnapshotId ?? dataSet.SnapshotId,
            WeixinWindows4Adapter.AdapterId,
            workspace.Workspace.Provenance,
            AccountIdentity.CandidateOnly);
    }

    public VoiceCatalogContext Context { get; }

    public async IAsyncEnumerable<ContactRecord> QueryContactsAsync(
        ContactQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await WeixinWindows4Adapter.OpenReadOnlyAsync(_contact, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = new List<string> { "username IS NOT NULL", "username <> ''", "username NOT LIKE '%@chatroom'" };
        if (query.Username is not null)
        {
            where.Add("username = $username");
            command.Parameters.AddWithValue("$username", query.Username);
        }

        if (query.SearchTerm is not null)
        {
            where.Add("(username LIKE $search ESCAPE '\\' OR alias LIKE $search ESCAPE '\\' OR remark LIKE $search ESCAPE '\\' OR nick_name LIKE $search ESCAPE '\\')");
            command.Parameters.AddWithValue("$search", "%" + EscapeLike(query.SearchTerm) + "%");
        }

        command.CommandText = $"""
            SELECT username, alias, remark, nick_name
            FROM contact
            WHERE {string.Join(" AND ", where)}
            ORDER BY username
            {(query.MaximumResults is null ? string.Empty : "LIMIT $maximum")};
            """;
        if (query.MaximumResults is not null)
        {
            command.Parameters.AddWithValue("$maximum", query.MaximumResults.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var username = reader.GetString(0);
            var alias = ReadNullableString(reader, 1);
            var remark = ReadNullableString(reader, 2);
            var nickname = ReadNullableString(reader, 3);
            yield return new ContactRecord(
                username,
                username,
                FirstNonBlank(remark, nickname, alias, username),
                remark,
                alias,
                nickname,
                username,
                _contact.DatabasePath);
        }
    }

    public async IAsyncEnumerable<VoiceRecord> QueryVoicesAsync(
        VoiceQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(query);
        var username = RequireStableContact(query);
        if (query.Direction == VoiceDirection.Unknown)
        {
            throw new ArgumentException("The exact Weixin adapter accepts only incoming, outgoing, or no direction filter.", nameof(query));
        }

        var shardRows = new List<IReadOnlyList<MessageRow>>(_messages.Count);
        foreach (var artifact in _messages)
        {
            var rows = new List<MessageRow>();
            await ReadMessageRowsAsync(artifact, username, query, rows, cancellationToken).ConfigureAwait(false);
            shardRows.Add(rows);
        }

        // Each shard already applies an ordered per-shard LIMIT; the k-way
        // merge stops at the global MaximumResults instead of materializing
        // shardCount * N rows and re-sorting.
        var selected = MergeMessageRows(shardRows, query.MaximumResults).ToArray();
        if (selected.Where(static message => message.OriginSource == 2)
            .Select(message => message.SpeakerId ?? username)
            .Distinct(StringComparer.Ordinal)
            .Any(speaker => !string.Equals(speaker, username, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("A one-to-one incoming export resolved more than one speaker; group-chat association is refused.");
        }
        var requestedKeys = selected
            .Select(static message => new AssociationKey(message.LocalId, message.ServerId, message.CreateTime))
            .ToHashSet();
        var payloads = await ReadMediaRowsAsync(username, requestedKeys, query.DeepScan, cancellationToken).ConfigureAwait(false);
        foreach (var message in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new AssociationKey(message.LocalId, message.ServerId, message.CreateTime);
            payloads.TryGetValue(key, out var payload);
            var direction = message.OriginSource switch
            {
                2 => VoiceDirection.Incoming,
                5 => VoiceDirection.Outgoing,
                _ => throw new InvalidDataException($"Unsupported origin_source value '{message.OriginSource}' in verified voice table."),
            };
            var messagePrimaryKey = $"message:{message.ShardNumber}:{message.TableName}:{message.LocalId}:{message.ServerId}";
            var messageId = $"{message.ShardNumber}:{message.LocalId}:{message.ServerId}";
            yield return new VoiceRecord(
                messageId,
                username,
                DateTimeOffset.FromUnixTimeSeconds(message.CreateTime),
                direction,
                payload is { State: VoicePayloadState.Linked }
                    ? new VoicePayloadLocator("media", _media.ShardNumber, payload.RowId.ToString(CultureInfo.InvariantCulture))
                    : null,
                message.Artifact.DatabasePath,
                message.ShardNumber,
                message.ShardNumber?.ToString(CultureInfo.InvariantCulture) ?? message.Artifact.DatabasePath,
                Context.SnapshotId,
                Context.AdapterId,
                Context.AccountId,
                messagePrimaryKey,
                payload?.Sha256,
                payload?.ByteLength,
                MediaLinked: payload?.State == VoicePayloadState.Linked,
                SpeakerId: direction == VoiceDirection.Outgoing ? Context.AccountId : message.SpeakerId ?? username,
                DataSetId: Context.DatasetId,
                AdapterVersion: Context.AdapterVersion,
                DatabaseFingerprints: Context.DatabaseFingerprints,
                AdapterFamily: Context.AdapterFamily,
                AccountStableId: Context.AccountId,
                ConversationStableId: username,
                MessagePrimaryKey: messagePrimaryKey,
                MediaPrimaryKey: payload is null or { State: VoicePayloadState.Ambiguous }
                    ? null
                    : $"media:{username}:{message.LocalId}:{message.ServerId}:{message.CreateTime}",
                PayloadState: payload?.State ?? VoicePayloadState.Missing);
        }
    }

    public async ValueTask<Stream> OpenPayloadAsync(VoicePayloadLocator locator, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(locator);
        if (!string.Equals(locator.LogicalRole, "media", StringComparison.OrdinalIgnoreCase)
            || locator.ShardNumber != _media.ShardNumber
            || !long.TryParse(locator.BlobKey, NumberStyles.None, CultureInfo.InvariantCulture, out var rowId)
            || rowId <= 0)
        {
            throw new InvalidDataException("The payload locator is not valid for this verified media database.");
        }

        var connection = await WeixinWindows4Adapter.OpenReadOnlyAsync(_media, cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT voice_data FROM VoiceInfo WHERE rowid = $rowid;";
        command.Parameters.AddWithValue("$rowid", rowId);
        SqliteDataReader? reader = null;
        try
        {
            reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
            {
                throw new FileNotFoundException("The verified voice payload no longer exists in the media database.");
            }

            return new OwnedPayloadStream(reader.GetStream(0), reader, command, connection);
        }
        catch
        {
            if (reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }

            await command.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task ReadMessageRowsAsync(
        DatabaseArtifact artifact,
        string username,
        VoiceQuery query,
        ICollection<MessageRow> destination,
        CancellationToken cancellationToken)
    {
        var tableName = "Msg_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(username))).ToLowerInvariant();
        var schemaTable = artifact.Schema.Objects.SingleOrDefault(schemaObject => string.Equals(
            Weixin41155SchemaSignature.NormalizeMessageTableName(schemaObject.Name),
            tableName,
            StringComparison.Ordinal));
        if (schemaTable is null || !WeixinWindows4Adapter.IsMessageTable(schemaTable))
        {
            return;
        }

        await using var connection = await WeixinWindows4Adapter.OpenReadOnlyAsync(artifact, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = new List<string> { "local_type = 34" };
        if (query.Direction is not null)
        {
            where.Add("origin_source = $originSource");
            command.Parameters.AddWithValue("$originSource", query.Direction == VoiceDirection.Incoming ? 2 : 5);
        }

        if (query.FromUtc is not null)
        {
            where.Add("create_time >= $from");
            command.Parameters.AddWithValue("$from", query.FromUtc.Value.ToUnixTimeSeconds());
        }

        if (query.ToUtc is not null)
        {
            where.Add("create_time <= $to");
            command.Parameters.AddWithValue("$to", query.ToUtc.Value.ToUnixTimeSeconds());
        }

        if (query.MaximumResults is not null)
        {
            command.Parameters.AddWithValue("$limit", query.MaximumResults.Value);
        }

        command.CommandText = $"""
            SELECT local_id, server_id, create_time, real_sender_id, origin_source
            FROM "{tableName}"
            WHERE {string.Join(" AND ", where)}
            ORDER BY create_time, local_id, server_id
            {(query.MaximumResults is null ? string.Empty : "LIMIT $limit")};
            """;
        var rows = new List<MessageRow>();
        var senderIds = new HashSet<long>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var senderRowId = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
                if (senderRowId is { } senderId)
                {
                    senderIds.Add(senderId);
                }

                rows.Add(new MessageRow(
                    artifact,
                    artifact.ShardNumber,
                    tableName,
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(4),
                    senderRowId,
                    SpeakerId: null));
            }
        }

        // One-to-one chats only ever need the two rowids in Name2Id that
        // resolve this conversation; the full map is never loaded.
        var speakers = await ReadNameMapAsync(connection, senderIds, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            destination.Add(row with { SpeakerId = row.SenderRowId is { } sid ? speakers.GetValueOrDefault(sid) : null });
        }
    }

    private async Task<IReadOnlyDictionary<AssociationKey, MediaRow>> ReadMediaRowsAsync(
        string username,
        IReadOnlyCollection<AssociationKey> requestedKeys,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<AssociationKey, MediaRow>();
        if (requestedKeys.Count == 0)
        {
            return result;
        }

        await using var connection = await WeixinWindows4Adapter.OpenReadOnlyAsync(_media, cancellationToken).ConfigureAwait(false);
        foreach (var batch in requestedKeys.Chunk(128))
        {
            await using var command = connection.CreateCommand();
            var values = new StringBuilder();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    values.Append(',');
                }

                values.Append($"($local{index}, $server{index}, $time{index})");
                command.Parameters.AddWithValue($"$local{index}", batch[index].LocalId);
                command.Parameters.AddWithValue($"$server{index}", batch[index].ServerId);
                command.Parameters.AddWithValue($"$time{index}", batch[index].CreateTime);
            }

            command.CommandText = $"""
                WITH requested(local_id, server_id, create_time) AS (VALUES {values})
                SELECT v.rowid, v.local_id, v.svr_id, v.create_time, length(v.voice_data), v.voice_data
                FROM VoiceInfo AS v
                INNER JOIN Name2Id AS n ON n.rowid = v.chat_name_id
                INNER JOIN requested AS r
                    ON r.local_id = v.local_id
                   AND r.server_id = v.svr_id
                   AND r.create_time = v.create_time
                WHERE n.user_name = $username;
                """;
            command.Parameters.AddWithValue("$username", username);
            await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = new AssociationKey(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
                var payload = await ReadMediaPayloadAsync(reader, deepScan, cancellationToken).ConfigureAwait(false);
                AddMediaRow(result, key, payload);
            }
        }

        return result;
    }

    private static async Task<MediaRow> ReadMediaPayloadAsync(
        SqliteDataReader reader,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        var rowId = reader.GetInt64(0);
        if (reader.IsDBNull(4) || reader.IsDBNull(5) || reader.GetInt64(4) == 0)
        {
            return new MediaRow(rowId, 0, null, VoicePayloadState.Empty);
        }

        var length = reader.GetInt64(4);
        await using var stream = reader.GetStream(5);
        var prefix = new byte[SilkHeader.MaxLength];
        var prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            var read = await stream.ReadAsync(prefix.AsMemory(prefixLength), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            prefixLength += read;
        }

        if (!SilkHeader.IsValid(prefix.AsSpan(0, prefixLength)))
        {
            return new MediaRow(rowId, length, null, VoicePayloadState.InvalidHeader);
        }

        if (!deepScan)
        {
            return new MediaRow(rowId, length, null, VoicePayloadState.Linked);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix.AsSpan(0, prefixLength));
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
            }

            return new MediaRow(
                rowId,
                length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                VoicePayloadState.Linked);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void AddMediaRow(
        IDictionary<AssociationKey, MediaRow> rows,
        AssociationKey key,
        MediaRow row)
    {
        if (!rows.TryAdd(key, row))
        {
            rows[key] = new MediaRow(0, 0, null, VoicePayloadState.Ambiguous);
        }
    }

    private static async Task<IReadOnlyDictionary<long, string>> ReadNameMapAsync(
        SqliteConnection connection,
        IReadOnlyCollection<long> senderIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, string>();
        if (senderIds.Count == 0)
        {
            return result;
        }

        foreach (var batch in senderIds.Chunk(128))
        {
            await using var command = connection.CreateCommand();
            var placeholders = string.Join(',', batch.Select((_, index) => $"$sender{index}"));
            for (var index = 0; index < batch.Length; index++)
            {
                command.Parameters.AddWithValue($"$sender{index}", batch[index]);
            }

            command.CommandText = $"""
                SELECT rowid, user_name
                FROM Name2Id
                WHERE rowid IN ({placeholders});
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result[reader.GetInt64(0)] = reader.GetString(1);
            }
        }

        return result;
    }

    private static string RequireStableContact(VoiceQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.ContactUsername)
            || string.IsNullOrWhiteSpace(query.ContactId)
            || string.IsNullOrWhiteSpace(query.ConversationId)
            || !string.Equals(query.ContactUsername, query.ContactId, StringComparison.Ordinal)
            || !string.Equals(query.ContactUsername, query.ConversationId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Voice queries require one exact, consistently bound ContactUsername, ContactId, and ConversationId.", nameof(query));
        }

        if (query.ContactUsername.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Group and @chatroom contacts are not supported by the first exact adapter.");
        }

        return query.ContactUsername;
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string FirstNonBlank(params string?[] values)
        => values.First(static value => !string.IsNullOrWhiteSpace(value))!;

    private sealed record MessageRow(
        DatabaseArtifact Artifact,
        int? ShardNumber,
        string TableName,
        long LocalId,
        long ServerId,
        long CreateTime,
        long OriginSource,
        long? SenderRowId = null,
        string? SpeakerId = null);

    private sealed record MediaRow(long RowId, long ByteLength, string? Sha256, VoicePayloadState State);

    /// <summary>
    /// Merges per-shard ordered rows into one global order and stops at the
    /// global MaximumResults. Each shard already applied an ordered per-shard
    /// LIMIT, so memory stays bounded by shardCount * N and rows beyond the
    /// global N are never read from any shard.
    /// </summary>
    private static IEnumerable<MessageRow> MergeMessageRows(
        IReadOnlyList<IReadOnlyList<MessageRow>> shards,
        int? maximum)
    {
        var queue = new PriorityQueue<MessageRow, MessageRowKey>(new MessageRowKeyComparer());
        var cursor = new int[shards.Count];
        for (var index = 0; index < shards.Count; index++)
        {
            if (shards[index].Count > 0)
            {
                queue.Enqueue(shards[index][0], MessageRowKey.Create(shards[index][0], index));
                cursor[index] = 1;
            }
        }

        var emitted = 0;
        while (queue.TryDequeue(out var row, out var key))
        {
            yield return row;
            emitted++;
            if (maximum is not null && emitted >= maximum.Value)
            {
                yield break;
            }

            var shardIndex = key.ShardOrdinal;
            if (cursor[shardIndex] < shards[shardIndex].Count)
            {
                var next = shards[shardIndex][cursor[shardIndex]++];
                queue.Enqueue(next, MessageRowKey.Create(next, shardIndex));
            }
        }
    }

    private readonly record struct MessageRowKey(
        long CreateTime,
        int? ShardNumber,
        long LocalId,
        long ServerId,
        int ShardOrdinal)
    {
        internal static MessageRowKey Create(MessageRow row, int ordinal)
            => new(row.CreateTime, row.ShardNumber, row.LocalId, row.ServerId, ordinal);
    }

    private sealed class MessageRowKeyComparer : IComparer<MessageRowKey>
    {
        public int Compare(MessageRowKey x, MessageRowKey y)
        {
            var result = x.CreateTime.CompareTo(y.CreateTime);
            if (result != 0)
            {
                return result;
            }

            result = Nullable.Compare(x.ShardNumber, y.ShardNumber);
            if (result != 0)
            {
                return result;
            }

            result = x.LocalId.CompareTo(y.LocalId);
            if (result != 0)
            {
                return result;
            }

            result = x.ServerId.CompareTo(y.ServerId);
            if (result != 0)
            {
                return result;
            }

            return x.ShardOrdinal.CompareTo(y.ShardOrdinal);
        }
    }

    private readonly record struct AssociationKey(long LocalId, long ServerId, long CreateTime);

    private static class SilkHeader
    {
        internal const int MaxLength = 10;
        private static readonly byte[] Raw = "#!SILK_V3"u8.ToArray();

        internal static bool IsValid(ReadOnlySpan<byte> prefix)
            => prefix.StartsWith(Raw)
                || prefix.Length >= Raw.Length + 1
                    && prefix[0] == 0x02
                    && prefix[1..].StartsWith(Raw);
    }
}

internal sealed class OwnedPayloadStream : Stream
{
    private readonly Stream _inner;
    private readonly SqliteDataReader _reader;
    private readonly SqliteCommand _command;
    private readonly SqliteConnection _connection;
    private bool _disposed;

    internal OwnedPayloadStream(Stream inner, SqliteDataReader reader, SqliteCommand command, SqliteConnection connection)
    {
        _inner = inner;
        _reader = reader;
        _command = command;
        _connection = connection;
    }

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => !_disposed && _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _inner.Dispose();
            _reader.Dispose();
            _command.Dispose();
            _connection.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _inner.DisposeAsync().ConfigureAwait(false);
            await _reader.DisposeAsync().ConfigureAwait(false);
            await _command.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}
