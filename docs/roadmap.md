# Roadmap

## Completed main path

1. Stable, group-validated snapshots use content-addressed IDs and preserve all
   source files outside the reserved `.wechatvoice/` metadata directory.
2. Shareable probes, verified raw snapshots, verified materializations, and
   executable local workspaces are separate trust boundaries.
3. The one-shot elevated Key Broker verifies the current-user Weixin process
   tree, exact 4.1.11.55 executable identity, and exact versioned WCDB module.
   It accepts no caller-selected PID, address, length, command, or raw-key
   output.
4. The 4.1.11.55 Profile recognizes the observed protected WCDB key specs,
   validates candidates using exact SQLCipher page profiles, binds keys to
   database-group fingerprints, and clears sensitive buffers.
5. The fixed SQLCipher Worker materializes 20 required databases into ordinary
   SQLite. Every output passes header, schema, hash, and `PRAGMA quick_check`
   acceptance. The migration-only `migrate/unspportmsg.db` is the sole narrow
   intentionally-ignored source status.
6. Materialization automatically emits a verified local workspace containing a
   stable account identity; no plaintext key file is created.
7. The exact Weixin 4.x Adapter supports contact list/search, direction-aware
   voice scanning, strict message/media association, streaming BLOB reads, and
   raw SILK export.
8. Export uses stable source keys, content-addressed paths, per-run flushed
   Journals, committed Manifests, hash verification, and repeat-run safe skips.
   Each source BLOB is read once; run manifests inherit full materialization
   provenance.
9. Broker and worker binaries are trusted through split Development/Release
   policies: release requires Authenticode + pinned publisher + hash-bound
   publish manifests + a non-user-writable install directory, and the publish
   smoke signs or fails closed. Private Broker staging is DACL-restricted to
   SYSTEM and Administrators.
10. Account identity is a confirmed candidate, never a silent path pick: the
    detected account must be explicitly confirmed (`--account` or the
    `IAccountConfirmation` port) before privileged materialization.
11. Stable error codes (`ErrorCode` + `ErrorCatalog`) cross the Broker/CLI
    boundary with `IsRetryable`/`SuggestedAction`/`NonSensitiveTechnicalContext`;
    presentation layers own localized text.
12. Workspace verification builds one `VerifiedFileIndex` shared by dataset
    probing, provenance verification, and the adapter; maximum results merge
    globally across message shards, and Name2Id reads stay bounded to the
    resolved conversation.
13. The Desktop guided flow carries one `ExportProjectSession`, requires an
    explicit contact selection, fixes the first-pass direction to incoming,
    binds Scan and Export to an immutable result-set fingerprint, and serializes
    high-cost operations through an application coordinator.
14. The Desktop supports source discovery, recent Workspace verification and
    recovery, manifest-checked materialized Workspace deletion, typed trust
    preflight, awaitable UI dispatch, and headless page construction tests.
15. Release packaging has one complete-layout entry point with RID-locked
   restore, post-signature bundle manifests, recursive package closure, SBOM,
   checksums, and protected-directory trust smoke.
16. The Desktop flow now keeps Workspace JSON paths in the project session,
    invalidates Scan/Export plans when the source, Workspace, contact, or query
    changes, and forwards the immutable maximum-result limit through Export.
17. Workspace catalogs hold read-only database file leases for their lifetime;
    content verification is cached per lease and payload reads perform a cheap
    identity/metadata recheck. Existing export artifacts are still hashed before
    a repeat-run skip; the artifact index is bookkeeping only.
18. Materialization commit markers are monotonic and cross-process locked;
   cancellation uses the Broker pipe-disconnect protocol, and Broker/Worker
   trust failures are typed before reaching Desktop presentation.
19. The exact 4.1.11.55 adapter re-derives account self-identity evidence from
   the verified `contact.encrypt_username = username` row on every catalog
   open; persisted user confirmation never upgrades technical evidence.
20. Materialization page inputs follow the application session when a new
   snapshot is created, and state-transition retries validate their declared
   predecessor set even when the requested state is already durable.
21. Dataset curation keeps successful export separate from training selection;
    users can filter by duration/size/quality, choose duplicate representatives,
    persist opaque Selection Profiles, and reproduce a stable Selection
    Fingerprint without exposing account identifiers or local paths.
22. The CLI composition root is split into focused command and support files;
    command behavior and the shared verified workflow composition remain
    unchanged.
23. The Desktop source-snapshot flow performs bounded automatic data-source
    discovery on page activation, selects exactly one complete account only
    when the result is unambiguous, requires an explicit choice for multiple
    accounts, and generates an opaque per-operation snapshot destination under
    LocalApplicationData. Weixin running-state checks, truncated-discovery
    warnings, validated manual fallback, and cancellable page activation are
    covered by Desktop and Avalonia Headless tests.
24. The Desktop is now **resume-first**: a shared `IProjectStateWorkflow` and
    `ProjectStateWorkflow` (inspect + resume) in `WeChatVoice.Workflows`
    classifies existing local project state as
    `ProjectStageState` (`ValidReusable` / `Recoverable` / `Stale` / `Invalid` /
    `Busy` / `Missing`) and reuses verified workspaces, adopts recoverable
    materializations, or repairs a lost/corrupt Workspace JSON without
    re-running Snapshot, UAC, or materialization. The Desktop opens on a
    resume page that presents the classification and the user's continue choice
    only; the workspace output factory inspects the occupied canonical path
    before ever allocating a new GUID copy. Second-run reuse (no re-snapshot /
    no re-materialization / no re-UAC) is covered by workflow integration tests
    and Desktop/Avalonia-Headless tests.
25. Managed storage lifecycle: a shared `IStorageLifecycleWorkflow` +
    `StorageLifecycleWorkflow` plus a read-only `ManagedStorageInventory`
    classifies app-owned storage (`Snapshots` / `Workspaces` / temp roots) into
    `StorageAssetKind` (`Transient` / `RecoverableIntermediate` /
    `ReusableIntermediate` / `UserAsset` / `DerivedUserAsset`), totals sizes,
    and exposes a two-step preview-then-clean. Cleanup only removes independent
    transient objects and expired-recoverable workspaces (routed through the
    workspace deletion boundary), never skips active locks or reparse points,
    and never auto-deletes raw exports or datasets. The Desktop "存储管理" page and the CLI `storage inventory|preview|cleanup` commands both consume the
    shared workflow; inventory/preview/preview/cleanup/reparse-point/lock tests
    are covered in the Workflows suite.
26. Storage lifecycle is now complete on the startup/orphan front: a
    `StartupOrphanSweeper` clears stale app-owned staging and decoder/duration
    temp payloads (refusing reparse points and only touching known roots), the
    `ManagedStorageInventory` detects redundant snapshots by content
    fingerprint (`SnapshotManifest.SnapshotId`) and surfaces
    `DuplicateSnapshotGroup`s through the workflow, and the Desktop recent index
    self-repairs by dropping entries that reference workspaces/snapshots no
    longer on disk (`RecentWorkspaceStore.RepairDangling`) on startup. None of
    these paths delete raw exports or datasets, and all are covered by startup
    sweep, duplicate-detection, and recent-repair tests.
27. Dataset curation is now training-ready: an `AudioBuildProfile` (sample rate,
    mono/stereo, normalization, `Version`) carries a SHA-256
    `ProfileFingerprint`, and a build with that profile decodes the selected
    SILK into validated PCM WAV under `audio/*.wav` while preserving the source
    SILK as the source of truth. A `IVoiceDecoderFactory` (with a
    `SilkVoiceDecoderFactory` implementation) supplies profile-specific-rate
    decoders, and the build identity combines the selection fingerprint with the
    audio profile fingerprint so a changed profile produces a new build
    identity rather than overwriting an old one. WAV build results record the
    decoder identity, and verify/repair/delete now cover WAV derived artifacts
    (repair rebuilds only derived metadata and re-verifies WAV in place).
    Direction-aware curation (`DatasetDirectionScope` Incoming/Outgoing/Both)
    replaces the incoming-only first-pass filter. The Desktop "数据集整理" page
    exposes direction selection, WAV build settings (sample rate / mono), and a
    per-item audio preview that decodes SILK to a transient WAV and plays it via
    the Windows `winmm` API with cleanup on stop. WAV build / fingerprint /
    verify / repair / delete and preview decode are covered by Core and Desktop
    headless tests.
28. P1 audio quality analysis: a bounded, streaming `VoiceQualityAnalysis` /
    `VoiceQualityAnalyzer` reads a decoded PCM WAV in one pass and computes
    decode success, duration, sample rate / channels / PCM format, silence
    ratio, clipping ratio, RMS and peak, deriving structured quality flags
    (empty, silent, clipping, low-level, decode-failed, duration-mismatch). The
    WAV dataset build merges these derived flags into each entry's
    `QualityFlags`, and Dataset Repair recomputes them from the on-disk WAV so
    rebuilt metadata stays faithful. The analyzer and its WAV-build enrichment
    are covered by Core unit tests and integration tests.
29. Scan / prepared-selection persistence: a `ScanCacheService` keeps scan
    results retrievable across app restarts by binding them to the verified
    workspace identity and the query fingerprint (catalog + query +
    selection-engine + duration-resolver). Records are serialized as JSONL and
    larger sets are rehydrated through a temporary spool; a SHA-256 manifest
    guards integrity on read. `VoiceScanWorkflow` reuses an intact cache hit and
    only re-scans on a fingerprint change or verification failure, writing fresh
    results back afterward. The cache lives under `Data/scan-cache` and is
    included in the managed transient inventory. Cache reuse/miss and
    round-trip/corruption are covered by Core and workflow tests.
30. User-facing refresh semantics: the Resume home page now distinguishes five
    distinct actions (`RefreshActionCatalog`: continue / refresh-from-source /
    re-scan / re-analyze / rebuild-dataset). Each `RefreshAction` documents its
    own scope (what it reuses, what it redoes, and what it never touches) so
    users never treat "continue" and "re-run everything" the same. A lightweight
    `INavigationService` lets the Resume view model route each action to the
    page that owns that workflow (source snapshot / scan / dataset curation),
    while the `IProjectStateWorkflow` decision remains authoritative. The five
    actions, their routing, and the navigation bridge are covered by Core and
    Desktop tests.

## Next product work

### Seed-VC fine-tuning (P0/P1 complete)

The Dataset Build is now the input boundary for Seed-VC. The shared workflow
and CLI expose `seedvc doctor`, `seedvc prepare`, `seedvc train`, and
`seedvc infer`.
Preparation verifies the build manifest, filters invalid/short WAV files,
normalizes to mono PCM, splits long recordings into 1–30 second clips, keeps
phone anchors with an explicit weight, and persists a content/profile
fingerprint so a verified result is reused. Training remains an external
Seed-VC checkout: the host passes a fixed argv list to `train.py`, captures a
bounded local log, rewrites `log_dir` into the app-owned run directory, and
records run provenance/checkpoint hashes. Existing runs are resumed only when
the preparation and config hashes match; completed runs with valid checkpoints
are reused without starting Python. The Desktop panel, explicit-argv
inference bridge, folder pickers, fingerprint-keyed local settings, restored
run/checkpoint state, and conversion playback controls are implemented.
Python/CUDA remains an external dependency; no model weights or user audio are
stored in the repository.

The cross-platform toolchain surface is global and reusable. `seedvc config
show/set` writes one per-user `toolchain.json`; Linux uses XDG config semantics
and may store an OpenSSH alias plus remote paths without copying credentials.
CLI arguments and `WECHATVOICE_*` variables override that file. Remote
execution is intentionally a separate follow-up workflow; the current local
training path already consumes the same resolved configuration.

### Remaining Seed-VC work

1. Run the documented manual RTX 3060 Ti acceptance path with the user's
   installed Seed-VC checkout and confirm one checkpoint plus one conversion.
2. Keep upstream Seed-VC version/config hashes in each run manifest when the
   host environment is available; this is release-audit metadata, not a new
   training backend.
3. Do not add RVC/GPT-SoVITS, automatic cloud downloads, or a second audio
   preparation pipeline to this integration.

1. Recover voice duration from a verified message metadata field only if the
   user supplies schema evidence and test data for that field. Until then,
   `--resolve-durations` and the Desktop option use the reviewed external SILK
   decoder boundary and report unknown duration when it is not configured or
   cannot decode.
2. The resident decoder boundary now supports the reviewed
   `wechatvoice-decoder-jsonl-v1` protocol through
   `WECHATVOICE_SILK_DECODER_WORKER_PATH`; keep strict RIFF/PCM validation and
   never overwrite SILK. A decoder that does not implement this protocol uses
   the existing one-shot compatibility path.
3. Maintain installer certificate-rotation and multi-release update policy.
   The ZIP package remains a diagnostic-only artifact; formal Broker
   distribution uses the signed MSIX installed to a protected directory. The
   AppX Publisher subject is fixed by the protected release environment while
   certificate/public-key pairs may rotate through the independent policy.

The first installer lifecycle is now scripted: a signed package has an
update-manifest binding, `install-msix.ps1` verifies that binding and can run
the installed ordinary-user trust smoke, `rollback-msix.ps1` requires a lower
package version when metadata is available, and `uninstall-msix.ps1` removes
only the current-user AppX registration. None of these commands delete
Snapshot, Workspace, Export, or LocalApplicationData data. Certificate
rotation remains a release-operator policy: a rotated certificate must be
present in the Broker bundle manifest and the installed MSIX publisher before
an update is accepted.

New Weixin versions require a new exact process/module Profile and schema
evidence. There is no unknown-version heuristic fallback.
