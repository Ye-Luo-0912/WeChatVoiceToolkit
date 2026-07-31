# Architecture

`WeChatVoice.Core` contains stable models and ports. `Application` orchestrates
voice exports. `Infrastructure` implements group-level snapshotting, readonly
SQLite schema inspection, lease-backed file export, and external SILK decoding.
`Windows` contains local process primitives. `Cli` composes the allowed
operations, while `ElevatedHelper` has a deliberately tiny JSON Lines protocol.

`DataSetProbe` is a shareable report and never carries executable local paths.
`LocalWorkspace` is the separate local-only binding consumed by adapters and
must remain under `.wechatvoice/`. `BuiltInAdapters` is the single adapter
composition root shared by probing, resolution, doctor, and future hosts.

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
the application only copies streams and coordinates the workflow. Export keys
include snapshot, adapter, account, shard, conversation, and source message
identity. Existing artifacts are reused only when their SHA-256 matches; run
history is kept under `runs/` and `latest.manifest.json` is the only rolling
pointer.

`dataset probe` discovers database files, pairs message/media shards, records
WAL/SHM completeness, hashes every DB/WAL/SHM member, and emits a deterministic
database-group and Schema Fingerprint. The output is shareable and redacts local
paths. `workspace create` repeats the probe into an executable local binding.
Adapter candidates are reported only from the central registry; filename
discovery never chooses a schema mapping by itself. `voice scan` is metadata-only
and must precede the raw SILK export path.

Encrypted or proprietary database containers are reported as
`encrypted-or-non-sqlite`. `IDatabaseMaterializer` is the only boundary allowed
to turn a raw snapshot into ordinary SQLite; its first implementation uses a
fixed external process protocol and validates SQLite headers plus
`PRAGMA quick_check` before returning a workspace.
