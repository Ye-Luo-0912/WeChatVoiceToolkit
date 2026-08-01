using WeChatVoice.Core.Models;

namespace WeChatVoice.Infrastructure.Adapters;

/// <summary>
/// The deliberately exact schema contract for Weixin Windows 4.1.11.55.
/// Matching is structural (ordered columns, affinity declaration, nullability,
/// primary-key bit and defaults) rather than a loose set-of-column check.
/// </summary>
internal static class Weixin41155SchemaSignature
{
    internal const string Id = "weixin-4.1.11.55-schema-v1";

    private static readonly ColumnSpec[] Contact =
    [
        I("id", true), T("username"), I("local_type"), T("alias"), T("encrypt_username"), I("flag"),
        I("delete_flag"), I("verify_flag"), T("remark"), T("remark_quan_pin"), T("remark_pin_yin_initial"),
        T("nick_name"), T("pin_yin_initial"), T("quan_pin"), T("big_head_url"), T("small_head_url"),
        T("head_img_md5"), I("chat_room_notify"), I("is_in_chat_room"), T("description"), B("extra_buffer"),
        I("chat_room_type"),
    ];

    private static readonly ColumnSpec[] MessageName = [T("user_name", true), I("is_session")];

    private static readonly ColumnSpec[] Message =
    [
        I("local_id", true), I("server_id"), I("local_type"), I("sort_seq"), I("real_sender_id"),
        I("create_time"), I("status"), I("upload_status"), I("download_status"), I("server_seq"),
        I("origin_source"), T("source"), T("message_content"), T("compress_content"), B("packed_info_data"),
        I("WCDB_CT_message_content", defaultValue: "NULL"), I("WCDB_CT_source", defaultValue: "NULL"),
    ];

    private static readonly ColumnSpec[] MediaName = [T("user_name", true)];
    private static readonly ColumnSpec[] Media =
    [I("chat_name_id"), I("create_time"), I("local_id"), I("svr_id"), B("voice_data"), T("data_index", defaultValue: "'0'")];

    internal static bool MatchesContact(SchemaSnapshot schema)
        => MatchesTable(schema, "contact", Contact)
            && HasIndex(schema, "contact", "contact_localType", false, "local_type");

    internal static bool MatchesMessageName(SchemaSnapshot schema)
        => MatchesTable(schema, "Name2Id", MessageName)
            && HasIndex(schema, "Name2Id", "sqlite_autoindex_Name2Id_1", true, "user_name");

    internal static bool MatchesMediaName(SchemaSnapshot schema)
        => MatchesTable(schema, "Name2Id", MediaName)
            && HasIndex(schema, "Name2Id", "sqlite_autoindex_Name2Id_1", true, "user_name");

    internal static bool MatchesMedia(SchemaSnapshot schema)
        => MatchesTable(schema, "VoiceInfo", Media)
            && HasIndex(schema, "VoiceInfo", "VoiceInfo_UNIQUE_INDEX", true, "chat_name_id", "create_time", "local_id", "data_index")
            && HasIndex(schema, "VoiceInfo", "VoiceInfo_INDEX", false, "chat_name_id", "svr_id");

    internal static bool MatchesMessageTable(SchemaSnapshot schema, string tableName)
        => MatchesTable(schema, tableName, Message)
            && HasIndex(schema, tableName, tableName + "_TYPE_SEQ", false, "local_type", "sort_seq")
            && HasIndex(schema, tableName, tableName + "_SORTSEQ", false, "sort_seq")
            && HasIndex(schema, tableName, tableName + "_SERVERID", false, "server_id")
            && HasIndex(schema, tableName, tableName + "_SENDERID", false, "real_sender_id");

    internal static string NormalizeMessageTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName) || !tableName.StartsWith("Msg_", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return "Msg_" + tableName[4..].ToLowerInvariant();
    }

    private static bool MatchesTable(SchemaSnapshot schema, string tableName, IReadOnlyList<ColumnSpec> expected)
    {
        var table = schema.Objects.SingleOrDefault(item => item.Kind == SchemaObjectKind.Table
            && string.Equals(item.Name, tableName, StringComparison.OrdinalIgnoreCase));
        if (table is null || table.Columns.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var actual = table.Columns[index];
            var wanted = expected[index];
            if (actual.Ordinal != index
                || !string.Equals(actual.Name, wanted.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(NormalizeType(actual.DeclaredType), wanted.Type, StringComparison.Ordinal)
                || actual.IsPrimaryKey != wanted.PrimaryKey
                || actual.IsNullable != wanted.Nullable
                || !string.Equals(NormalizeDefault(actual.DefaultValue), wanted.DefaultValue, StringComparison.Ordinal)
                || actual.IsHidden
                || actual.IsGenerated)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasIndex(SchemaSnapshot schema, string tableName, string name, bool unique, params string[] columns)
    {
        var index = schema.Indexes.SingleOrDefault(item => string.Equals(item.TableName, tableName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return index is not null
            && index.IsUnique == unique
            && index.Columns.Count(c => !string.IsNullOrWhiteSpace(c.ColumnName)) == columns.Length
            && index.Columns.Where(c => !string.IsNullOrWhiteSpace(c.ColumnName)).Select(c => c.ColumnName!).SequenceEqual(columns, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeType(string? type) => (type ?? string.Empty).Trim().ToUpperInvariant();

    private static string NormalizeDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    private sealed record ColumnSpec(string Name, string Type, bool PrimaryKey = false, bool Nullable = true, string DefaultValue = "");

    private static ColumnSpec I(string name, bool primaryKey = false, string defaultValue = "") => new(name, "INTEGER", primaryKey, true, defaultValue);
    private static ColumnSpec T(string name, bool primaryKey = false, string defaultValue = "") => new(name, "TEXT", primaryKey, true, defaultValue);
    private static ColumnSpec B(string name, bool primaryKey = false, string defaultValue = "") => new(name, "BLOB", primaryKey, true, defaultValue);
}
