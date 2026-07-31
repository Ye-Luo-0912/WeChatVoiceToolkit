using WeChatVoice.Windows;

namespace WeChatVoice.KeyAcquisition.Models;

/// <summary>
/// A key is scoped to one database group and is held only by a zeroing buffer.
/// The public model never exposes a byte array or a serializable key value.
/// </summary>
public sealed record DatabaseKeyBinding(
    string SnapshotId,
    string AccountStableId,
    string DatabaseGroupFingerprint,
    string RelativeDatabasePath,
    int? ShardNumber,
    string EncryptionProfileId,
    SensitiveBuffer ProtectedKeyMaterial);

public sealed record ValidatedDatabaseKey(
    string DatabaseGroupFingerprint,
    string ProfileId,
    SensitiveBuffer KeyMaterial);
