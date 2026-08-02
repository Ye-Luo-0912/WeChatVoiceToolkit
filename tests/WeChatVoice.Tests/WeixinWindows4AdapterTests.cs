using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WeChatVoice.Application;
using WeChatVoice.Core.Models;
using WeChatVoice.Infrastructure.Adapters;
using WeChatVoice.Infrastructure.Sqlite;

namespace WeChatVoice.Tests;

public sealed class WeixinWindows4AdapterTests
{
    private const string AccountId = "wxid_owner";
    private const string ContactId = "wxid_peer";

    [Fact]
    public async Task Adapter_closes_contact_scan_and_streamed_payload_path_with_strict_association()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("workspace");
        await CreateFixtureAsync(root);
        var verified = await CreateVerifiedWorkspaceAsync(root);
        var adapter = new WeixinWindows4Adapter();

        var match = adapter.Probe(verified.DataSet);
        Assert.True(match.IsMatch, match.Reason);
        Assert.Equal(100, match.Score);

        await using var catalog = await adapter.OpenAsync(verified, CancellationToken.None);
        Assert.Equal(AccountId, catalog.Context.AccountId);
        Assert.Equal("snapshot-test", catalog.Context.SnapshotId);
        // The path-derived candidate was verified against contact and Name2Id
        // but is not yet proven to be the account itself; hosts must confirm.
        Assert.Equal(AccountIdentityState.Candidate, catalog.Context.AccountIdentity.State);

        var exactContacts = await CollectAsync(catalog.QueryContactsAsync(new ContactQuery(Username: ContactId), CancellationToken.None));
        var contact = Assert.Single(exactContacts);
        Assert.Equal(ContactId, contact.ContactId);
        Assert.Equal(ContactId, contact.ConversationId);
        Assert.Equal("Peer Remark", contact.DisplayName);
        Assert.Equal("peer_alias", contact.WeChatId);

        var searched = await CollectAsync(catalog.QueryContactsAsync(new ContactQuery(SearchTerm: "Remark"), CancellationToken.None));
        Assert.Contains(searched, item => item.ContactId == ContactId);

        var incoming = await CollectAsync(catalog.QueryVoicesAsync(
            StableQuery(VoiceDirection.Incoming, deepScan: true),
            CancellationToken.None));
        Assert.Equal(4, incoming.Count);
        var linked = Assert.Single(incoming, static record => record.PayloadByteLength == 10);
        var empty = Assert.Single(incoming, static record => record.PayloadByteLength == 0);
        var unlinked = Assert.Single(incoming, static record => record.PayloadState == VoicePayloadState.Missing);
        var invalid = Assert.Single(incoming, static record => record.PayloadState == VoicePayloadState.InvalidHeader);
        Assert.False(empty.MediaLinked);
        Assert.False(invalid.MediaLinked);
        Assert.Null(unlinked.PayloadLocator);
        Assert.Null(unlinked.SourceStableKey);
        Assert.NotNull(linked.PayloadLocator);
        Assert.NotNull(linked.SourceStableKey);
        Assert.Equal(ContactId, linked.SpeakerId);
        Assert.Equal(10, linked.PayloadByteLength);

        var expectedPayload = Encoding.ASCII.GetBytes("#!SILK_V3\n");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant(), linked.PayloadSha256);
        await using var payload = await catalog.OpenPayloadAsync(linked.PayloadLocator!, CancellationToken.None);
        using var payloadCopy = new MemoryStream();
        await payload.CopyToAsync(payloadCopy);
        Assert.Equal(expectedPayload, payloadCopy.ToArray());

        var scan = await new VoiceScanService(catalog).ScanAsync(StableQuery(VoiceDirection.Incoming));
        Assert.Equal(4, scan.MatchedVoiceCount);
        Assert.Equal(1, scan.UnassociatedMediaCount);
        Assert.Equal(1, scan.EmptyBlobCount);
        Assert.Equal(1, scan.InvalidHeaderCount);

        var outgoing = await CollectAsync(catalog.QueryVoicesAsync(
            StableQuery(VoiceDirection.Outgoing, deepScan: true),
            CancellationToken.None));
        var sent = Assert.Single(outgoing);
        Assert.True(sent.MediaLinked);
        Assert.Equal(AccountId, sent.SpeakerId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_060), sent.OccurredAtUtc);
    }

    [Fact]
    public async Task Adapter_preserves_workspace_user_confirmation_without_upgrading_evidence()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("workspace");
        await CreateFixtureAsync(root);
        var local = await new LocalWorkspaceCreator().CreateAsync(root, CancellationToken.None);
        var dataSet = new WeChatDataSet(
            local.DataSet.DataSetId,
            AccountId,
            local.DataSet.Databases,
            "snapshot-test",
            local.DataSet.AdapterId);
        var provenance = new MaterializationProvenance(
            "snapshot-test",
            "materialized-test",
            "sqlcipher-e_sqlcipher-worker",
            "e_sqlcipher-test",
            "backend-hash",
            "manifest-hash",
            "weixin-windows-4.1.11.55-wcdb-protected-spec-v2",
            "4.1.11.55",
            "ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d",
            "ab925b9428239def44b252d970c337034d75e66b27eb5529633dc10669fc796a",
            "sid-fingerprint");
        var workspace = new LocalWorkspace(
            local.WorkspaceId,
            local.SourceRoot,
            dataSet,
            local.CreatedAtUtc,
            local.Issues,
            local.AdapterCandidates,
            provenance,
            AccountIdentity: new AccountIdentity(
                AccountIdentityState.Candidate,
                null,
                UserConfirmationState.Confirmed));

        await using var catalog = await new WeixinWindows4Adapter().OpenAsync(
            new VerifiedLocalWorkspace(workspace, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(AccountIdentityState.Candidate, catalog.Context.AccountIdentity.State);
        Assert.Equal(UserConfirmationState.Confirmed, catalog.Context.AccountIdentity.UserConfirmation);
        Assert.Null(catalog.Context.AccountIdentity.ConfirmedBy);
    }

    [Fact]
    public async Task Adapter_marks_identity_confirmed_when_self_identity_row_is_present()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("workspace");
        await CreateFixtureAsync(root);
        // The account's own contact row is the only row whose encrypt_username
        // equals its username (live-validated discriminator, 2026-08-01).
        await ExecuteAsync(Path.Combine(root, "databases", "contact", "contact.db"),
            $"UPDATE contact SET encrypt_username = username WHERE username = '{AccountId}';");
        var verified = await CreateVerifiedWorkspaceAsync(root);
        var adapter = new WeixinWindows4Adapter();

        await using var catalog = await adapter.OpenAsync(verified, CancellationToken.None);
        Assert.Equal(AccountIdentityState.Confirmed, catalog.Context.AccountIdentity.State);
        Assert.Equal("contact-self-identity-row", catalog.Context.AccountIdentity.ConfirmedBy);
    }

    [Fact]
    public async Task Adapter_keeps_candidate_identity_without_a_self_identity_row()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("workspace");
        await CreateFixtureAsync(root);
        var verified = await CreateVerifiedWorkspaceAsync(root);
        var adapter = new WeixinWindows4Adapter();

        await using var catalog = await adapter.OpenAsync(verified, CancellationToken.None);
        // Without the verified self-identity discriminator the account is
        // present in the indexes but not proven to be itself; identity stays
        // Candidate and no export path is blocked.
        Assert.Equal(AccountIdentityState.Candidate, catalog.Context.AccountIdentity.State);
        Assert.Null(catalog.Context.AccountIdentity.ConfirmedBy);
    }

    [Fact]
    public async Task Adapter_never_confirms_identity_from_a_foreign_self_identity_row()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("workspace");
        await CreateFixtureAsync(root);
        // A foreign contact carries the self-equal pattern; the account row
        // itself does not. Exactly-once self-row matching must not confirm.
        await ExecuteAsync(Path.Combine(root, "databases", "contact", "contact.db"),
            $"UPDATE contact SET encrypt_username = username WHERE username = '{ContactId}';");
        var verified = await CreateVerifiedWorkspaceAsync(root);
        var adapter = new WeixinWindows4Adapter();

        await using var catalog = await adapter.OpenAsync(verified, CancellationToken.None);
        Assert.Equal(AccountIdentityState.Candidate, catalog.Context.AccountIdentity.State);
    }

    [Fact]
    public async Task Adapter_without_deep_scan_reports_length_and_header_without_a_source_hash()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("workspace");
        await CreateFixtureAsync(root);
        var verified = await CreateVerifiedWorkspaceAsync(root);
        await using var catalog = await new WeixinWindows4Adapter().OpenAsync(verified, CancellationToken.None);

        var shallow = await CollectAsync(catalog.QueryVoicesAsync(
            StableQuery(VoiceDirection.Incoming, deepScan: false),
            CancellationToken.None));
        var linked = Assert.Single(shallow, static record => record.PayloadByteLength == 10);

        // The export path must not pre-read the BLOB for a content hash: the
        // single streaming read happens at commit time instead.
        Assert.Equal(VoicePayloadState.Linked, linked.PayloadState);
        Assert.Equal(10, linked.PayloadByteLength);
        Assert.Null(linked.PayloadSha256);
    }

    [Fact]
    public async Task Adapter_merges_message_shards_under_a_global_maximum_results()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("workspace");
        await CreateSecondMessageShardAsync(root);
        await CreateFixtureAsync(root);
        var verified = await CreateVerifiedWorkspaceAsync(root);
        await using var catalog = await new WeixinWindows4Adapter().OpenAsync(verified, CancellationToken.None);

        // Global incoming times: shard0@0, shard1@30, shard1@80, shard0@120,
        // ... (fixture local 11 is outgoing). A global limit of 3 must span
        // both shards and stop immediately instead of reading every shard to
        // completion.
        var limitedQuery = new VoiceQuery(
            ContactId,
            VoiceDirection.Incoming,
            MaximumResults: 3,
            ContactUsername: ContactId,
            ContactId: ContactId);
        var limited = await CollectAsync(catalog.QueryVoicesAsync(limitedQuery, CancellationToken.None));

        Assert.Equal(3, limited.Count);
        Assert.Equal([1_700_000_000, 1_700_000_030, 1_700_000_080], limited.Select(static record => record.OccurredAtUtc.ToUnixTimeSeconds()).ToArray());
        Assert.Equal([0, 1, 1], limited.Select(static record => record.ShardNumber).ToArray());
    }

    [Fact]
    public async Task Adapter_preflights_incoming_speakers_before_yielding_any_record()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("workspace");
        await CreateSecondMessageShardAsync(root);
        await CreateFixtureAsync(root);
        var secondShard = Path.Combine(root, "databases", "message", "message_1.db");
        var tableName = "Msg_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(ContactId))).ToLowerInvariant();
        await ExecuteAsync(secondShard, $"""
            INSERT INTO Name2Id (rowid, user_name, is_session) VALUES (3, 'wxid_other', 1);
            UPDATE "{tableName}"
            SET real_sender_id = 3
            WHERE local_id = 20;
            """);

        var verified = await CreateVerifiedWorkspaceAsync(root);
        await using var catalog = await new WeixinWindows4Adapter().OpenAsync(verified, CancellationToken.None);
        var yielded = 0;

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in catalog.QueryVoicesAsync(
                StableQuery(VoiceDirection.Incoming),
                CancellationToken.None))
            {
                yielded++;
            }
        });

        Assert.Contains("more than one speaker", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, yielded);
    }

    private static async Task CreateSecondMessageShardAsync(string root)
    {
        await InitializeProviderAsync(root);
        var messagePath = Path.Combine(root, "databases", "message", "message_1.db");
        Directory.CreateDirectory(Path.GetDirectoryName(messagePath)!);
        var tableName = "Msg_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(ContactId))).ToLowerInvariant();
        await ExecuteAsync(messagePath, $$"""
            CREATE TABLE Name2Id (user_name TEXT PRIMARY KEY, is_session INTEGER);
            INSERT INTO Name2Id (rowid, user_name, is_session) VALUES
                (1, 'wxid_owner', 1), (2, 'wxid_peer', 1);
            CREATE TABLE "{{tableName}}" (
                local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, sort_seq INTEGER, real_sender_id INTEGER,
                create_time INTEGER, status INTEGER, upload_status INTEGER, download_status INTEGER,
                server_seq INTEGER, origin_source INTEGER, source TEXT, message_content TEXT, compress_content TEXT,
                packed_info_data BLOB, WCDB_CT_message_content INTEGER DEFAULT NULL, WCDB_CT_source INTEGER DEFAULT NULL
            );
            CREATE INDEX "{{tableName}}_TYPE_SEQ" ON "{{tableName}}"(local_type, sort_seq);
            CREATE INDEX "{{tableName}}_SORTSEQ" ON "{{tableName}}"(sort_seq);
            CREATE INDEX "{{tableName}}_SERVERID" ON "{{tableName}}"(server_id);
            CREATE INDEX "{{tableName}}_SENDERID" ON "{{tableName}}"(real_sender_id);
            INSERT INTO "{{tableName}}"
                (local_id, server_id, local_type, sort_seq, real_sender_id, create_time, status, upload_status,
                 download_status, server_seq, origin_source)
            VALUES
                (20, 200, 34, 1, 2, 1700000030, 3, 0, 0, 1, 2),
                (21, 201, 34, 2, 2, 1700000080, 3, 0, 0, 2, 2),
                (22, 202, 34, 3, 2, 1700000140, 3, 0, 0, 3, 2);
            """);
    }

    [Fact]
    public async Task Adapter_refuses_workspace_without_stable_account_identity()
    {
        using var temporary = new TestTemporaryDirectory();
        var root = temporary.CreateDirectory("workspace");
        await CreateFixtureAsync(root);
        var local = await new LocalWorkspaceCreator().CreateAsync(root, CancellationToken.None);
        var verified = await new LocalWorkspaceVerifier().VerifyAsync(local, CancellationToken.None);
        var adapter = new WeixinWindows4Adapter();

        Assert.True(adapter.Probe(verified.DataSet).IsMatch);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await adapter.OpenAsync(verified, CancellationToken.None));
        Assert.Contains("stable Weixin account ID", exception.Message, StringComparison.Ordinal);
    }

    private static VoiceQuery StableQuery(VoiceDirection direction, bool deepScan = false)
        => new(
            ContactId,
            direction,
            ContactUsername: ContactId,
            ContactId: ContactId,
            DeepScan: deepScan);

    private static async Task<VerifiedLocalWorkspace> CreateVerifiedWorkspaceAsync(string root)
    {
        var local = await new LocalWorkspaceCreator().CreateAsync(root, CancellationToken.None);
        var dataSet = new WeChatDataSet(
            local.DataSet.DataSetId,
            AccountId,
            local.DataSet.Databases,
            "snapshot-test",
            local.DataSet.AdapterId);
        var provenance = new MaterializationProvenance(
            "snapshot-test",
            "materialized-test",
            "sqlcipher-e_sqlcipher-worker",
            "e_sqlcipher-test",
            "backend-hash",
            "manifest-hash",
            "weixin-windows-4.1.11.55-wcdb-protected-spec-v2",
            "4.1.11.55",
            "ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d",
            "ab925b9428239def44b252d970c337034d75e66b27eb5529633dc10669fc796a",
            "sid-fingerprint");
        var withAccount = new LocalWorkspace(
            local.WorkspaceId,
            local.SourceRoot,
            dataSet,
            local.CreatedAtUtc,
            local.Issues,
            local.AdapterCandidates,
            provenance);
        return new VerifiedLocalWorkspace(withAccount, DateTimeOffset.UtcNow);
    }

    private static async Task CreateFixtureAsync(string root)
    {
        await InitializeProviderAsync(root);
        var contactPath = Path.Combine(root, "databases", "contact", "contact.db");
        var messagePath = Path.Combine(root, "databases", "message", "message_0.db");
        var mediaPath = Path.Combine(root, "databases", "message", "media_0.db");
        Directory.CreateDirectory(Path.GetDirectoryName(contactPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(messagePath)!);

        await ExecuteAsync(contactPath, """
            CREATE TABLE contact (
                id INTEGER PRIMARY KEY, username TEXT, local_type INTEGER, alias TEXT, encrypt_username TEXT, flag INTEGER,
                delete_flag INTEGER, verify_flag INTEGER, remark TEXT, remark_quan_pin TEXT,
                remark_pin_yin_initial TEXT, nick_name TEXT, pin_yin_initial TEXT, quan_pin TEXT,
                big_head_url TEXT, small_head_url TEXT, head_img_md5 TEXT, chat_room_notify INTEGER,
                is_in_chat_room INTEGER, description TEXT, extra_buffer BLOB, chat_room_type INTEGER
            );
            CREATE INDEX contact_localType ON contact(local_type);
            INSERT INTO contact (id, username, alias, remark, nick_name) VALUES
                (1, 'wxid_owner', 'owner_alias', '', 'Owner'),
                (2, 'wxid_peer', 'peer_alias', 'Peer Remark', 'Peer Nick');
            """);

        var tableName = "Msg_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(ContactId))).ToLowerInvariant();
        await ExecuteAsync(messagePath, $$"""
            CREATE TABLE Name2Id (user_name TEXT PRIMARY KEY, is_session INTEGER);
            INSERT INTO Name2Id (rowid, user_name, is_session) VALUES
                (1, 'wxid_owner', 1), (2, 'wxid_peer', 1);
            CREATE TABLE "{{tableName}}" (
                local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, sort_seq INTEGER, real_sender_id INTEGER,
                create_time INTEGER, status INTEGER, upload_status INTEGER, download_status INTEGER,
                server_seq INTEGER, origin_source INTEGER, source TEXT, message_content TEXT, compress_content TEXT,
                packed_info_data BLOB, WCDB_CT_message_content INTEGER DEFAULT NULL, WCDB_CT_source INTEGER DEFAULT NULL
            );
            CREATE INDEX "{{tableName}}_TYPE_SEQ" ON "{{tableName}}"(local_type, sort_seq);
            CREATE INDEX "{{tableName}}_SORTSEQ" ON "{{tableName}}"(sort_seq);
            CREATE INDEX "{{tableName}}_SERVERID" ON "{{tableName}}"(server_id);
            CREATE INDEX "{{tableName}}_SENDERID" ON "{{tableName}}"(real_sender_id);
            INSERT INTO "{{tableName}}"
                (local_id, server_id, local_type, sort_seq, real_sender_id, create_time, status, upload_status,
                 download_status, server_seq, origin_source)
            VALUES
                (10, 100, 34, 1, 2, 1700000000, 3, 0, 0, 1, 2),
                (11, 101, 34, 2, 1, 1700000060, 4, 0, 0, 2, 5),
                (12, 102, 34, 3, 2, 1700000120, 3, 0, 0, 3, 2),
                (13, 103, 1,  4, 2, 1700000180, 3, 0, 0, 4, 2),
                (14, 104, 34, 5, 2, 1700000240, 3, 0, 0, 5, 2),
                (15, 105, 34, 6, 2, 1700000300, 3, 0, 0, 6, 2);
            """);

        await ExecuteAsync(mediaPath, """
            CREATE TABLE Name2Id (user_name TEXT PRIMARY KEY);
            INSERT INTO Name2Id (rowid, user_name) VALUES (1, 'wxid_peer');
            CREATE TABLE VoiceInfo (
                chat_name_id INTEGER, create_time INTEGER, local_id INTEGER, svr_id INTEGER,
                voice_data BLOB, data_index TEXT DEFAULT '0'
            );
            CREATE UNIQUE INDEX VoiceInfo_UNIQUE_INDEX ON VoiceInfo(chat_name_id, create_time, local_id, data_index);
            CREATE INDEX VoiceInfo_INDEX ON VoiceInfo(chat_name_id, svr_id);
            CREATE TABLE TimeStamp (timestamp INTEGER);
            """);
        await using var media = await OpenAsync(mediaPath);
        await using var insert = media.CreateCommand();
        insert.CommandText = """
            INSERT INTO VoiceInfo (chat_name_id, create_time, local_id, svr_id, voice_data, data_index)
            VALUES ($chat, $time, $local, $server, $payload, '0');
            """;
        insert.Parameters.AddWithValue("$chat", 1);
        insert.Parameters.Add("$time", SqliteType.Integer);
        insert.Parameters.Add("$local", SqliteType.Integer);
        insert.Parameters.Add("$server", SqliteType.Integer);
        insert.Parameters.Add("$payload", SqliteType.Blob);
        await InsertMediaAsync(insert, 1_700_000_000, 10, 100, Encoding.ASCII.GetBytes("#!SILK_V3\n"));
        await InsertMediaAsync(insert, 1_700_000_060, 11, 101, Encoding.ASCII.GetBytes("#!SILK_V3\nout"));
        await InsertMediaAsync(insert, 1_700_000_120, 12, 999, Encoding.ASCII.GetBytes("#!SILK_V3\nwrong"));
        await InsertMediaAsync(insert, 1_700_000_240, 14, 104, []);
        await InsertMediaAsync(insert, 1_700_000_300, 15, 105, Encoding.ASCII.GetBytes("not-silk"));
    }

    private static async Task InsertMediaAsync(SqliteCommand command, long time, long local, long server, byte[] payload)
    {
        command.Parameters["$time"].Value = time;
        command.Parameters["$local"].Value = local;
        command.Parameters["$server"].Value = server;
        command.Parameters["$payload"].Value = payload;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InitializeProviderAsync(string root)
    {
        var path = Path.Combine(root, "provider-init.db");
        await File.WriteAllBytesAsync(path, []);
        try
        {
            await new SqliteSchemaInspector().InspectAsync(path, CancellationToken.None);
        }
        catch (SqliteException)
        {
            // Provider initialization occurs before an empty database is rejected.
        }

        File.Delete(path);
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }
}
