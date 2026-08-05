# WeChatVoiceToolkit agent guardrails

This project is a Windows/.NET 10 foundation for exporting voice media from a
user-supplied, lawfully accessible WeChat data source.

- Do not guess database schemas, table names, key derivation, or encryption
  settings. Add a schema adapter only after the user supplies verified schema
  metadata and test data.
- Thin UI hosts are allowed and expected (the Avalonia Desktop is the current
  host alongside the CLI), but a UI must never bypass a Verified boundary
  (VerifiedRawSnapshot / VerifiedMaterialization / VerifiedLocalWorkspace), a
  security protocol (Broker binary trust, named-pipe process identity, no
  direct SQLite/process-memory access from UI code), or an Application Workflow
  (WeChatVoice.Workflows). UI code composes workflows and ports only; it does
  not re-implement verification, materialization, or key handling.
- Keep database inspection read-only.
- Preserve original SILK media; decoded WAV files are derived artifacts and
  must never overwrite them.
- Keep the elevated diagnostic helper free of memory-reading and decryption
  duties. The separate one-shot Key Broker may grow through reviewed Profile
  and materializer registries; do not freeze product versions, database formats,
  or output mappings into the transport. The non-negotiable floor is: no
  caller-selected process/PID/address/read length, no VM write/injection/remote
  thread, no arbitrary command execution, no raw-key response or persistence,
  and no heuristic fallback for an unmatched Profile. Development external
  materializers require explicit `--allow-untrusted-backend` and may not accept
  a `--key-file`.
- Treat snapshots, exports, logs, and manifests as potentially sensitive.
  Keep them out of source control.
- Ordinary logs must never record contact usernames, key material, memory
  contents, or database data. Recent-workspace metadata is persisted only under
  LocalApplicationData. Desktop diagnostics show stages, error codes, and
  durations only.
- A snapshot is valid only after group-level before/after inventory checks over
  database, WAL, SHM, and related files. Keep snapshot metadata in the reserved
  `.wechatvoice/` directory and exclude it from source enumeration.
- New WeChat integrations must use `WeChatDataSet` and
  `IWeChatDataSetAdapter`/`IVoiceCatalog`; never infer a payload relationship
  from a single `SchemaSnapshot`.
- Treat `RawSnapshot`, `MaterializationResult`, and `LocalWorkspace` JSON as
  untrusted data. Pipeline boundaries must use `VerifiedRawSnapshot`,
  `VerifiedMaterialization`, and `VerifiedLocalWorkspace` respectively.
- `IVoiceCatalog` is `IAsyncDisposable`; every host that opens one must dispose
  it with `await using`.
- Export application code must use `IExportItemLease`; do not add absolute-path
  writes back into `VoiceExportService`.
- Export Journal completion is valid only after the manifest files are written
  and the `manifest-committed` event is flushed. A processing-completed event
  is not a committed run.
- Contact selection for scan/export must use the exact stable internal username;
  remarks and nicknames are display/search fields only.
- The first usable export chain is decrypted DB bundle -> incoming voice -> raw
  SILK -> run manifest. Do not make WAV decoding a prerequisite.
- Dataset probing may discover filenames and emit adapter candidates, but it
  must never choose an unverified schema mapping by convention.

## Development principles

- Prefer code reuse over parallel one-off implementations. Extend an existing
  interface, validator, framing primitive, file index, or test fixture when it
  already expresses the required behavior; keep one authoritative path for
  verification and persistence rules.
- Design for high performance without sacrificing correctness. Keep database
  access read-only and bounded, use streaming I/O for BLOBs and exports, avoid
  repeated hashing or process startup, apply filters and limits in SQL, and
  measure before introducing complex concurrency or low-level optimization.
- Design for high maintainability. Keep responsibilities small and explicit,
  preserve verified-type boundaries, make security and provenance data flow
  visible, prefer deterministic behavior and actionable errors, and add focused
  tests whenever a boundary, invariant, or performance guarantee changes.
- New code must fit the existing composition root and lifecycle contracts. Do
  not add hidden global state, duplicate registries, implicit provider changes,
  or compatibility shims unless a concrete migration requirement exists.
- WeChatVoice.Workflows is the composition boundary shared by the CLI and the
  Desktop host. Product flows live there as workflows (EnvironmentAssessment,
  Snapshot, Materialization, Workspace, ContactDiscovery, VoiceScan,
  VoiceExport); hosts map OperationProgress/OperationError to their own
  presentation and never inline Infrastructure composition.
- The ordinary Desktop source-snapshot flow must abstract internal Weixin
  layout terms: automatically discover supported account directories on page
  activation, select a single complete candidate only when unambiguous, require
  explicit selection for multiple accounts, and generate an opaque safe
  snapshot destination under LocalApplicationData. `db_storage` and complete
  paths belong only in validated advanced details or diagnostics; a bounded or
  truncated discovery must never be presented as complete.
