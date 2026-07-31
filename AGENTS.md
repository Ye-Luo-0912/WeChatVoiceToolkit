# WeChatVoiceToolkit agent guardrails

This project is a Windows/.NET 10 foundation for exporting voice media from a
user-supplied, lawfully accessible WeChat data source.

- Do not guess database schemas, table names, key derivation, or encryption
  settings. Add a schema adapter only after the user supplies verified schema
  metadata and test data.
- Do not add UI in this phase.
- Keep database inspection read-only.
- Preserve original SILK media; decoded WAV files are derived artifacts and
  must never overwrite them.
- The elevated helper may expose only `ping`, `capabilities`, and
  `list-wechat-processes`. Never add arbitrary command execution, process
  memory access, key export, or database decryption to its protocol. Route two
  uses the separate one-shot `WeChatVoice.KeyBroker.exe` boundary; it must be
  launched explicitly with `runas`, accept only its fixed request schema, and
  never return raw keys. Until a verified Weixin build profile and database
  validation suite exist, the broker must fail closed with
  `profile_unavailable` rather than scan memory. Development external
  materializers require an explicit `--allow-untrusted-backend` flag and may
  not accept a `--key-file`.
- Treat snapshots, exports, logs, and manifests as potentially sensitive.
  Keep them out of source control.
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
