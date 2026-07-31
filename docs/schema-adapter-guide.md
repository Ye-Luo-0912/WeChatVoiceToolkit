# Schema adapter guide

An adapter must implement `IWeChatDataSetAdapter` and operate only on a
verified `VerifiedLocalWorkspace` (and its `WeChatDataSet`). The data set should include every related
`DatabaseArtifact` (message, media, contact, and any shard), its SHA-256, and a
read-only `SchemaSnapshot`. Register it through `BuiltInAdapters`; do not create
per-command adapter lists.

`Probe` must return no match for an unverified schema. `OpenAsync` returns an
`IVoiceCatalog`; `QueryVoicesAsync` produces `VoiceRecord` values whose
`VoicePayloadLocator` identifies the media database and BLOB key. The catalog
also owns contact queries and payload streams. Contacts must expose a stable
internal username and conversation ID; CLI export selection is by exact
username, never by a potentially duplicated remark or nickname. Do not encode unverified table,
column, encryption, or shard assumptions in shared code.
