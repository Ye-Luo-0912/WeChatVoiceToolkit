# Architecture

`WeChatVoice.Core` contains stable models and ports. `Application` orchestrates
voice exports. `Infrastructure` implements group-level snapshotting, readonly
SQLite schema inspection, lease-backed file export, and external SILK decoding.
`Windows` contains local process primitives. `Cli` composes the allowed
operations, while `ElevatedHelper` has a deliberately tiny JSON Lines protocol.

Snapshots copy the complete source file group into a staging directory and
accept it only when the before/after inventory agrees. WeChat is required to be
closed unless the caller explicitly opts into `--allow-live-source`; manifests
live under `.wechatvoice/snapshot-manifest.json` and exclude that reserved
metadata directory from source enumeration.

Schema adapters operate on `WeChatDataSet`, not one database. A data set contains
message, media, contact, and shard artifacts. `IWeChatDataSetAdapter` opens an
`IVoiceCatalog`, whose voice records carry a `VoicePayloadLocator` that can point
from message metadata to a media database BLOB.

`IVoiceExportStore.BeginItemAsync` returns an export lease. The lease owns path
reservation, temporary files, commit, rollback, and final manifest persistence;
the application only copies streams and coordinates the workflow.
