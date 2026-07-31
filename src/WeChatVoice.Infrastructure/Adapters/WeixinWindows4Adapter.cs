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
    internal const string AdapterVersion = "4.1.11.55-schema-v1";

    private static readonly string[] ContactColumns =
    [
        "id", "username", "local_type", "alias", "encrypt_username", "flag", "delete_flag", "verify_flag",
        "remark", "remark_quan_pin", "remark_pin_yin_initial", "nick_name", "pin_yin_initial", "quan_pin",
        "big_head_url", "small_head_url", "head_img_md5", "chat_room_notify", "is_in_chat_room", "description",
        "extra_buffer", "chat_room_type",
    ];

    private static readonly string[] MessageNameColumns = ["user_name", "is_session"];
    private static readonly string[] MessageColumns =
    [
        "local_id", "server_id", "local_type", "sort_seq", "real_sender_id", "create_time", "status",
        "upload_status", "download_status", "server_seq", "origin_source", "source", "message_content",
        "compress_content", "packed_info_data", "WCDB_CT_message_content", "WCDB_CT_source",
    ];

    private static readonly string[] MediaNameColumns = ["user_name"];
    private static readonly string[] MediaColumns =
    [
        "chat_name_id", "create_time", "local_id", "svr_id", "voice_data", "data_index",
    ];

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
        => RoleEquals(artifact, "contact") && HasTable(artifact.Schema, "contact", ContactColumns);

    private static bool IsMediaArtifact(DatabaseArtifact artifact)
        => RoleEquals(artifact, "media")
            && HasTable(artifact.Schema, "Name2Id", MediaNameColumns)
            && HasTable(artifact.Schema, "VoiceInfo", MediaColumns);

    private static bool IsMessageArtifact(DatabaseArtifact artifact)
        => RoleEquals(artifact, "message")
            && HasTable(artifact.Schema, "Name2Id", MessageNameColumns)
            && artifact.Schema.Objects.Any(static schemaObject => IsMessageTable(schemaObject) && HasColumns(schemaObject, MessageColumns));

    internal static bool HasTable(SchemaSnapshot schema, string tableName, IReadOnlyCollection<string> columns)
        => schema.Objects.Any(schemaObject => schemaObject.Kind == SchemaObjectKind.Table
            && string.Equals(schemaObject.Name, tableName, StringComparison.OrdinalIgnoreCase)
            && HasColumns(schemaObject, columns));

    private static bool HasColumns(SchemaObjectSnapshot schemaObject, IReadOnlyCollection<string> columns)
    {
        var actual = schemaObject.Columns.Select(static column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return columns.All(actual.Contains);
    }

    internal static bool IsMessageTable(SchemaObjectSnapshot schemaObject)
    {
        const string prefix = "Msg_";
        if (schemaObject.Kind != SchemaObjectKind.Table
            || !schemaObject.Name.StartsWith(prefix, StringComparison.Ordinal)
            || schemaObject.Name.Length != prefix.Length + 32)
        {
            return false;
        }

        return schemaObject.Name.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
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
            WeixinWindows4Adapter.AdapterId);
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
        var where = new List<string> { "username IS NOT NULL", "username <> ''" };
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

        var messageRows = new List<MessageRow>();
        foreach (var artifact in _messages)
        {
            await ReadMessageRowsAsync(artifact, username, query, messageRows, cancellationToken).ConfigureAwait(false);
        }

        var ordered = messageRows
            .OrderBy(static row => row.CreateTime)
            .ThenBy(static row => row.ShardNumber)
            .ThenBy(static row => row.LocalId)
            .ThenBy(static row => row.ServerId);
        if (query.MaximumResults is not null)
        {
            ordered = ordered.Take(query.MaximumResults.Value).OrderBy(static row => row.CreateTime)
                .ThenBy(static row => row.ShardNumber)
                .ThenBy(static row => row.LocalId)
                .ThenBy(static row => row.ServerId);
        }

        var selected = ordered.ToArray();
        var payloads = await ReadMediaRowsAsync(username, cancellationToken).ConfigureAwait(false);
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
                payload is null ? null : new VoicePayloadLocator("media", _media.ShardNumber, payload.RowId.ToString(CultureInfo.InvariantCulture)),
                message.Artifact.DatabasePath,
                message.ShardNumber,
                message.ShardNumber?.ToString(CultureInfo.InvariantCulture) ?? message.Artifact.DatabasePath,
                Context.SnapshotId,
                Context.AdapterId,
                Context.AccountId,
                messagePrimaryKey,
                payload?.Sha256,
                payload?.ByteLength,
                MediaLinked: payload is not null,
                SpeakerId: direction == VoiceDirection.Outgoing ? Context.AccountId : message.SpeakerId ?? username,
                DataSetId: Context.DatasetId,
                AdapterVersion: Context.AdapterVersion,
                DatabaseFingerprints: Context.DatabaseFingerprints,
                AdapterFamily: Context.AdapterFamily,
                AccountStableId: Context.AccountId,
                ConversationStableId: username,
                MessagePrimaryKey: messagePrimaryKey,
                MediaPrimaryKey: payload is null ? null : $"media:{_media.ShardNumber}:{payload.RowId}");
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
        var schemaTable = artifact.Schema.Objects.SingleOrDefault(schemaObject => string.Equals(schemaObject.Name, tableName, StringComparison.Ordinal));
        if (schemaTable is null || !WeixinWindows4Adapter.IsMessageTable(schemaTable))
        {
            return;
        }

        await using var connection = await WeixinWindows4Adapter.OpenReadOnlyAsync(artifact, cancellationToken).ConfigureAwait(false);
        var speakers = await ReadNameMapAsync(connection, cancellationToken).ConfigureAwait(false);
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

        command.CommandText = $"""
            SELECT local_id, server_id, create_time, real_sender_id, origin_source
            FROM "{tableName}"
            WHERE {string.Join(" AND ", where)};
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var senderId = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
            destination.Add(new MessageRow(
                artifact,
                artifact.ShardNumber,
                tableName,
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(4),
                senderId is null ? null : speakers.GetValueOrDefault(senderId.Value)));
        }
    }

    private async Task<IReadOnlyDictionary<AssociationKey, MediaRow>> ReadMediaRowsAsync(
        string username,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<AssociationKey, MediaRow>();
        await using var connection = await WeixinWindows4Adapter.OpenReadOnlyAsync(_media, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.rowid, v.local_id, v.svr_id, v.create_time, length(v.voice_data), v.voice_data
            FROM VoiceInfo AS v
            INNER JOIN Name2Id AS n ON n.rowid = v.chat_name_id
            WHERE n.user_name = $username;
            """;
        command.Parameters.AddWithValue("$username", username);
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new AssociationKey(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
            var rowId = reader.GetInt64(0);
            if (reader.IsDBNull(4) || reader.IsDBNull(5))
            {
                AddMediaRow(result, key, new MediaRow(rowId, 0, EmptyPayloadSha256));
                continue;
            }

            var length = reader.GetInt64(4);
            await using var stream = reader.GetStream(5);
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            AddMediaRow(result, key, new MediaRow(rowId, length, sha256));
        }

        return result;
    }

    private static void AddMediaRow(
        IDictionary<AssociationKey, MediaRow> rows,
        AssociationKey key,
        MediaRow row)
    {
        if (!rows.TryAdd(key, row))
        {
            throw new InvalidDataException("The media database contains more than one payload for the same verified association key.");
        }
    }

    private static async Task<IReadOnlyDictionary<long, string>> ReadNameMapAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid, user_name FROM Name2Id WHERE user_name IS NOT NULL AND user_name <> '';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetInt64(0)] = reader.GetString(1);
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
        string? SpeakerId);

    private sealed record MediaRow(long RowId, long ByteLength, string Sha256);

    private readonly record struct AssociationKey(long LocalId, long ServerId, long CreateTime);

    private const string EmptyPayloadSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
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
