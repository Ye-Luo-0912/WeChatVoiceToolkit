# Current handoff

`main` now has a real end-to-end path for the exact signed Weixin Windows
4.1.11.55 build:

`VerifiedRawSnapshot -> restricted key acquisition -> verified materialization
-> VerifiedLocalWorkspace -> contact -> voice scan -> raw SILK + run manifest`.

The Profile ID is
`weixin-windows-4.1.11.55-wcdb-protected-spec-v2`. It binds the exact
Weixin.exe version/hash and the versioned `4.1.11.55/Weixin.dll` hash, scans the
verified current-user process tree under fixed limits, recognizes the observed
protected WCDB key specifications, and accepts candidates only after exact
first-page HMAC validation. The database encryption Profile is retained per
database group and passed only through the private Broker-to-Worker stdin
envelope. Key material is never persisted or returned.

Live validation on 2026-08-01 used a stable 21-database Snapshot. Twenty
databases materialized successfully and passed SQLite header, Schema Probe,
hash, and `PRAGMA quick_check`. Only `migrate/unspportmsg.db` lacked a validated
key; its migration-only status is the sole exact policy exception and is
recorded as `IntentionallyIgnored`. Do not broaden this exception.

The built-in `weixin-windows-4` Adapter is versioned
`4.1.11.55-schema-v1`. Verified evidence is:

- contacts come from `contact.contact` and stable selection uses `username`;
- per-conversation message tables are `Msg_<lowercase MD5(username)>`;
- `local_type=34` is voice, `origin_source=2` is incoming, and
  `origin_source=5` is outgoing for this exact schema;
- media comes from `media_0.db/VoiceInfo`, with conversation mapped through
  `Name2Id.rowid`;
- association requires conversation plus the complete
  `(local_id, server_id/svr_id, create_time)` tuple. Never use local ID alone;
- payload BLOBs are streamed and hashed; missing and empty media are distinct
  scan outcomes.

A real incoming scan found linked SILK rows and a real export completed with no
failures. Repeating the same export verified and skipped all existing files,
confirming stable-key idempotency. Local snapshots, decrypted workspaces,
contacts, exports, and Manifests remain ignored and must never be committed.

The current correctness hardening also includes: environment trust is a
materialization prerequisite; Workspace JSON paths and contact/query choices
are session-owned; Scan/Export share a result-set fingerprint and maximum
limit; Workspace catalogs hold read-only file leases; materialization state
transitions are monotonic and locked; and Broker cancellation closes the pipe
before attempting best-effort process cleanup.

The Desktop is now **resume-first**. `IProjectStateWorkflow` /
`ProjectStateWorkflow` (in `WeChatVoice.Workflows`) classify existing local
project state via `ProjectStageState` (`ValidReusable` / `Recoverable` /
`Stale` / `Invalid` / `Busy` / `Missing`) and reuse verified workspaces, adopt
recoverable materializations, or repair a lost/corrupt Workspace JSON without
re-snapshot, re-materialization, or re-UAC. The Desktop opens on a resume page
that only presents the classification and the user's continue choice; the
workspace output factory inspects the occupied canonical path before ever
allocating a new GUID copy. Keep the reuse/recover decision in the shared
workflow and never re-implement it in the UI. Reuse must always re-verify; file
existence is never treated as reusable state.

The managed storage lifecycle is now a shared product capability.
`IStorageLifecycleWorkflow` / `StorageLifecycleWorkflow` plus a read-only
`ManagedStorageInventory` classify app-owned storage under
`%LocalAppData%/WeChatVoiceToolkit` (Snapshots, Workspaces, temp/prepared
roots) into `StorageAssetKind` and expose Inventory / PreviewCleanup / Cleanup.
Cleanup only removes independent Transient objects and expired-recoverable
workspaces (routed through the workspace deletion boundary); it never touches
active locks, reparse points, or user assets (raw exports, curated datasets).
The Desktop "存储管理" page and the CLI `storage inventory|preview|cleanup`
commands both consume the shared workflow. Keep inspection read-only and never
let the Recent index become the ownership source of truth: manifest / state
markers classify objects, and a lost catalog is rediscovered from disk.

Dataset curation is now **training-ready**. `AudioBuildProfile` (sample rate,
mono/stereo, normalization, `Version`) carries a SHA-256 `ProfileFingerprint`;
a build with that profile decodes selected SILK into validated PCM WAV under
`audio/*.wav` while the source SILK stays the source of truth. `IVoiceDecoderFactory`
(implemented by `SilkVoiceDecoderFactory`) supplies profile-specific-rate
decoders. The build identity combines the selection fingerprint with the audio
profile fingerprint so a changed profile yields a new build identity, never an
overwrite. WAV build results record decoder identity, and verify/repair/delete
cover WAV derived artifacts (repair rebuilds only derived metadata and
re-verifies WAV in place). Direction-aware curation (`DatasetDirectionScope`
Incoming/Outgoing/Both) replaces the incoming-only first-pass filter. The
Desktop "数据集整理" page exposes direction selection, WAV build settings
(sample rate / mono), and a per-item audio preview that decodes SILK to a
transient WAV and plays it via the Windows `winmm` API with cleanup on stop.
When verifying a WAV build without re-passing the profile, `VerifyAsync` reads
the build manifest as the authoritative build identity. All WAV build /
fingerprint / verify / repair / delete and preview-decode paths are covered by
Core and Desktop headless tests.

P1 audio quality analysis is implemented (`VoiceQualityAnalysis` /
`VoiceQualityAnalyzer`): a bounded, streaming analyzer over decoded PCM WAV
computes decode success, duration, sample rate / channels / PCM format, silence
ratio, clipping ratio, RMS and peak, and derives structured quality flags
(empty, silent, clipping, low-level, decode-failed, duration-mismatch). It is
integrated into the WAV dataset build so each derived entry carries merged
quality flags, and Dataset Repair recomputes them from the on-disk WAV so the
rebuilt metadata stays faithful. The analyzer is covered by Core unit tests and
a WAV-build enrichment integration test.

Scan / prepared-selection persistence is implemented (`ScanCacheService`): scan
results are cached under the managed `Data/scan-cache` directory bound to the
verified workspace identity and the query fingerprint (catalog + query +
selection-engine + duration-resolver). Records serialize as JSONL (larger sets
rehydrate through a temporary spool), a SHA-256 manifest guards integrity on
read, and `VoiceScanWorkflow` reuses an intact hit so a later scan of the
unchanged workspace does not re-read the catalog. A changed fingerprint or
verification failure triggers a fresh scan that is written back to the cache.
The cache is included in the managed transient inventory and is covered by Core
round-trip/corruption tests plus workflow reuse/miss tests.

Run retention is implemented (`RunRetentionService` + `IRunRetentionWorkflow`):
`export run-retention preview|compact` classifies runs as `KeepRecent` /
`Referenced` / `Compactable`, keeps the most recent N complete runs, compacts
only the journal/transaction of older unreferenced runs (never deleting
committed manifests, CSV, artifact index, or the metadata-commit descriptor),
preserves journal for runs whose manifest is not yet committed, and refuses to
compact any run referenced by a dataset selection profile. Reparse-point
protection and post-compaction re-checks are covered by tests.

User-facing refresh semantics are implemented (`WeChatVoice.Core.Models.RefreshActionCatalog`):
the Resume home page renders five distinct actions — continue / refresh-from-source
/ re-scan / re-analyze / rebuild-dataset — each documenting what it reuses and
what it redoes. A lightweight `INavigationService` routes each action to the
page that owns that workflow while the `IProjectStateWorkflow` verify/reuse
decision stays authoritative. The catalog, routing, and navigation bridge are
covered by Core and Desktop tests.

Seed-VC integration is now complete through the documented P1 boundary. The
Dataset Build feeds a reusable, fingerprinted preparation directory; the
shared workflow exposes `doctor`, `prepare`, `train`, and `infer`; and the
Desktop dataset page provides environment check, preparation, train/resume,
checkpoint selection, conversion, open-run, and playback actions. Training
rewrites the upstream config `log_dir` into the application-local run root so
checkpoints are discoverable beside `train.log` and `run-manifest.json`.
Existing runs are accepted only when preparation/config hashes match, and a
completed run with a verified checkpoint is reused without launching Python.
Desktop settings persist tool paths, preparation, run name, checkpoint, audio
inputs, and the last conversion keyed by Dataset Build fingerprint. The
remaining acceptance is manual GPU validation with the user's Seed-VC
checkout; no model weights or real audio belong in Git.

Global toolchain configuration is shared by all hosts through
`SeedVcToolchainResolver`. On Linux it defaults to
`$XDG_CONFIG_HOME/wechatvoice/toolchain.json` or
`~/.config/wechatvoice/toolchain.json`; Windows and macOS use their standard
per-user application-data directories. Use `seedvc config show/set` for
Seed-VC, Python, FFmpeg and an optional OpenSSH host alias. SSH credentials
stay in the user's `~/.ssh/config` and agent; do not persist private keys or
passwords in toolkit configuration.

Before release work, run the RID-locked restore, CI Release build, format
check, complete tests, and `scripts/package-release.ps1`. The remaining product
work: decoder productization (Phase 3: optional packaged reviewed decoder).
Account
self-identity evidence is already
re-derived from the verified `encrypt_username = username` row; user
confirmation remains a separate state. The existing decoder boundary is
optional and configured through `WECHATVOICE_SILK_DECODER_PATH`; it is not a
license or schema guess. Do not add an unknown-version Profile, partial
message/media join, raw-key output, arbitrary process reader, or
caller-selected privileged executable.
