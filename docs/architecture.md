# Architecture

`WeChatVoice.Core` contains stable models and ports. `Application` orchestrates
voice exports. `Infrastructure` implements group-level snapshotting, readonly
SQLite schema inspection, lease-backed file export, and external SILK decoding.
`Windows` contains local process primitives. `Cli` composes the allowed
operations, while `ElevatedHelper` has a deliberately tiny JSON Lines protocol.

`DataSetProbe` is a shareable report and never carries executable local paths.
`LocalWorkspace` is the separate local-only binding consumed by adapters and
must remain under `.wechatvoice/`. It is untrusted when loaded from JSON until
`ILocalWorkspaceVerifier` rechecks its root, reparse-point boundaries, file
set, DB/WAL/SHM hashes, and group fingerprints; adapters receive only the
resulting `VerifiedLocalWorkspace`. `BuiltInAdapters` is the single adapter
composition root shared by probing, resolution, doctor, and future hosts.

The Desktop source-snapshot page is an orchestration host, not a database
browser. Its awaitable navigation lifecycle invokes bounded
`IWeixinDataSourceDiscovery` on first entry, keeps `WasTruncated` and the
visited-directory count, and requires an explicit account choice when the
discovery result is ambiguous. A single complete selectable candidate may be
selected automatically. The page stores only the verified source selection in
`ExportProjectSession`; its default snapshot destination is generated below
LocalApplicationData from an opaque account fingerprint and a unique operation
component. Raw source and output paths are details/advanced settings, not the
ordinary user workflow.

Snapshots copy the complete source file group into a staging directory and
accept it only when the before/after inventory agrees. WeChat is required to be
closed unless the caller explicitly opts into `--allow-live-source`; manifests
live under `.wechatvoice/snapshot-manifest.json` and exclude that reserved
metadata directory from source enumeration.

The Desktop always submits `AllowLiveSource: false` for its normal snapshot
action and rechecks the fixed Weixin process list immediately before invoking
the Snapshot workflow. A running Weixin process disables the action and is
presented as a typed `WeixinStillRunning` condition. Manual folder selection is
only a fallback: it must resolve to one validated `db_storage` layout, reject
reparse points and empty database trees, and pass the same source/output
non-overlap and capacity checks as automatic discovery.

Once the snapshot workflow completes, the Desktop materialization page derives
an application-owned Workspace output directory and Workspace JSON path from
the verified snapshot identity. These paths are prepared before the button is
enabled, so an empty output field cannot reach the materialization workflow as
an `InvalidRequest`.

Schema adapters operate on `WeChatDataSet`, not one database. A data set contains
message, media, contact, and shard artifacts. `IWeChatDataSetAdapter` opens an
`IAsyncDisposable IVoiceCatalog`, whose voice records carry a `VoicePayloadLocator` that can point
from message metadata to a media database BLOB.

`IVoiceExportStore.BeginItemAsync` returns an export lease. The lease owns path
reservation, temporary files, expected-content hash validation, atomic replace,
commit, and rollback; the application only copies streams and coordinates the
workflow. `SourceStableKey` excludes snapshot provenance and requires adapter
family, account, conversation, message, and media identities. A separate
catalog context records dataset, snapshot, adapter version, and database
fingerprints for audit. Original and decoded artifacts have independent
Missing/VerifiedExisting/Conflict states, so a later run can add a missing WAV
without rewriting verified SILK. Physical paths use only the stable-key hash,
not message time. Run history is appended and flushed as JSONL events under
`runs/`; `processing-completed` is distinct from `manifest-committed`, and
`latest.metadata-commit.json` is the only rolling metadata pointer. The public
`dataset.manifest.json` and private `manifest.private.json` are separate
products. A truncated final Journal
line is ignored by `voice export recover`.

`dataset probe` discovers database files, pairs message/media shards, records
WAL/SHM completeness, hashes every DB/WAL/SHM member, and emits a deterministic
database-group and Schema Fingerprint. The output is shareable and redacts local
paths. `workspace create` repeats the probe into an executable local binding.
Adapter candidates are reported only from the central registry; filename
discovery never chooses a schema mapping by itself. `voice scan` is metadata-only
and must precede the raw SILK export path.

Encrypted or proprietary database containers are reported as
`encrypted-or-non-sqlite`. `IDatabaseMaterializer` is the only boundary allowed
to turn a raw snapshot into ordinary SQLite; its first implementation verifies
the complete raw snapshot file set and hashes, uses a fixed external process
protocol, requires source-to-output database mappings, rejects reparse points
and pre-existing output targets, writes `.wechatvoice/materialization-manifest.json`,
and validates SQLite headers plus `PRAGMA quick_check` before the CLI creates a
local workspace. Formal backends are selected through
`BuiltInMaterializationBackends`; the development-only external backend is
available only behind `--allow-untrusted-backend` and requires
`.wechatvoice/materialization-output.json` with explicit source-to-output
database mappings.

The diagnostic `ElevatedHelper` remains a metadata-only JSONL service. Formal
`workspace materialize` creates a one-time current-user pipe and launches the
separate `WeChatVoice.KeyBroker.exe` (`runas`/UAC manifest). The request contains
only protocol version, RequestId, SnapshotId, and `acquire-and-materialize`.
Exact process/database behavior is supplied through reviewed registries, while
arbitrary memory coordinates, writable access, key output, and unknown-version
fallback remain impossible. The registry contains one live-validated Profile
for the exact signed Weixin 4.1.11.55 executable and versioned WCDB module.
It binds validated keys to database groups and invokes a fixed SQLCipher Worker
without persisting or returning key material.

The matching `weixin-windows-4` Adapter recognizes only the verified contact,
message, and media schemas. It selects contacts by stable internal username,
derives `Msg_<md5(username)>`, maps direction from exact observed values, and
requires conversation plus local ID, server ID, and creation time for media
association. Payloads are exposed as owning read-only streams so SQLite
connections are released when the caller disposes the stream.
