# WeChatVoiceToolkit

A .NET 10, Windows-focused foundation for safely inspecting user-provided
WeChat data snapshots and exporting voice media through version-specific schema
adapters.

The repository now contains a live-validated, end-to-end path for the exact
signed Weixin Windows 4.1.11.55 build: restricted in-memory key acquisition,
ephemeral SQLCipher materialization, verified local workspace creation,
contact lookup, voice audit, and idempotent raw SILK export. No plaintext key
file or UI is involved.

## Current commands

```powershell
dotnet run --project src/WeChatVoice.Cli -- doctor
dotnet run --project src/WeChatVoice.Cli -- snapshot create --source <directory> --output <workspace>
dotnet run --project src/WeChatVoice.Cli -- snapshot create --source <directory> --output <workspace> --allow-live-source
dotnet run --project src/WeChatVoice.Cli -- schema probe --database <database> --output <schema.json>
dotnet run --project src/WeChatVoice.Cli -- dataset probe --root .\decrypted-db --shareable-output .\dataset-probe.shareable.json
dotnet run --project src/WeChatVoice.Cli -- workspace create --root .\decrypted-db --output .\.wechatvoice\local-workspace.json
dotnet run --project src/WeChatVoice.Cli -- contact list --workspace .\.wechatvoice\local-workspace.json
dotnet run --project src/WeChatVoice.Cli -- contact search --workspace .\.wechatvoice\local-workspace.json --query wxid
dotnet run --project src/WeChatVoice.Cli -- voice scan --workspace .\.wechatvoice\local-workspace.json --contact-username wxid_xxx --direction incoming --from 2025-01-01
dotnet run --project src/WeChatVoice.Cli -- voice export --workspace .\.wechatvoice\local-workspace.json --contact-username wxid_xxx --direction incoming --format silk --output .\exports\peer
dotnet run --project src/WeChatVoice.Cli -- voice export recover --journal .\exports\peer\runs\<run-id>.jsonl
dotnet run --project src/WeChatVoice.Cli -- workspace verify --workspace .\.wechatvoice\local-workspace.json
dotnet run --project src/WeChatVoice.Cli -- workspace materialize --snapshot-directory .\raw-snapshot --backend weixin-windows-4 --output .\decrypted-db --workspace-output .\.wechatvoice\local-workspace.json
echo '{"requestId":"1","operation":"ping"}' | dotnet run --project src/WeChatVoice.ElevatedHelper
```

The CLI owns the one-shot UAC Broker exchange; `WeChatVoice.KeyBroker` is not a
stdin tool and never exposes key material.

Snapshots require recognized WeChat processes to be closed by default. The
explicit `--allow-live-source` opt-in marks the internal
`.wechatvoice/snapshot-manifest.json` as `potentiallyInconsistent`; the source
files including a user-provided `snapshot.json` are preserved. Snapshot acceptance is
group-level: the complete file set, length, modification time, and file
identity must match before and after an attempt.

`dataset probe` always writes a shareable report with redacted paths. It is not
executable. `workspace create` writes the local-only
`.wechatvoice/local-workspace.json`, which contains absolute database paths and
must not be uploaded or committed. Contact, scan, and export consume only this
workspace document.

`workspace verify` rechecks every path, reparse-point boundary, DB/WAL/SHM
length, hash, and database-group fingerprint before an adapter can open a
workspace. The built-in `weixin-windows-4` adapter matches only the verified
4.1.11.55 contact/message/media schema. It associates media only by
conversation plus `local_id`, `server_id`, and `create_time`; it never falls
back to a partial join.
`workspace materialize` is the single formal acquire-and-materialize entry.
It launches the installed one-shot Broker, verifies the exact executable and
versioned WCDB module, performs bounded acquisition, materializes ordinary
SQLite, and clears key buffers. A development-only external backend requires both
`--external-decryptor` and `--allow-untrusted-backend`; it accepts only
`--input-root`, `--output-root`, and an explicit source-to-output manifest. Key
files are deliberately not accepted. The host verifies the raw snapshot file
set and hashes, requires every source database to be mapped, then validates
SQLite headers and `PRAGMA quick_check` on every output database. It writes a
materialization manifest and creates the local workspace JSON as one closed
workflow.

Export output is idempotent by `SourceStableKey` (adapter family, account,
conversation, message primary key, and media primary key); physical paths use
only a content hash fan-out and never the message timestamp. Snapshot and
database hashes remain provenance only. Missing identity or media association
is rejected before payload access. Runs are stored under
`runs/<run-id>.manifest.json` and an append-and-flush
`runs/<run-id>.jsonl` journal; only `latest.manifest.json` is replaced.
The voice commands use exit code `0` for complete success or safe skips, `2`
for invalid parameters, `3` for item-level partial failure, `4` for no
matching records, `1` for run-level failure, and `130` for cancellation.

The exact path was validated on 2026-08-01 against a stable 21-database raw
snapshot: 20 business databases materialized and passed SQLite header, schema,
hash, and `PRAGMA quick_check`; the one unsupported migration-only auxiliary
database is explicitly recorded as intentionally ignored. A real incoming scan
and raw SILK export completed, and a second run safely skipped every
hash-matching artifact.

See [architecture.md](docs/architecture.md), [adr-0001-sqlite-runtime.md](docs/adr-0001-sqlite-runtime.md), [security.md](docs/security.md),
and [agent-handoff.md](docs/agent-handoff.md) before extending the project.
