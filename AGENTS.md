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
  `list-wechat-processes`. Never add arbitrary command execution, arbitrary
  process-memory access, key export, or database decryption to its protocol.
- Treat snapshots, exports, logs, and manifests as potentially sensitive.
  Keep them out of source control.
- A snapshot is valid only after group-level before/after inventory checks over
  database, WAL, SHM, and related files. Keep snapshot metadata in the reserved
  `.wechatvoice/` directory and exclude it from source enumeration.
- New WeChat integrations must use `WeChatDataSet` and
  `IWeChatDataSetAdapter`/`IVoiceCatalog`; never infer a payload relationship
  from a single `SchemaSnapshot`.
- Export application code must use `IExportItemLease`; do not add absolute-path
  writes back into `VoiceExportService`.
