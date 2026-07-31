# WeChatVoiceToolkit

A .NET 10, Windows-focused foundation for safely inspecting user-provided
WeChat data snapshots and exporting voice media through version-specific schema
adapters.

The repository intentionally does **not** contain a WeChat key extractor,
database decryption logic, guessed schema mappings, or a UI. Those capabilities
require verified, user-provided version and schema information.

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
dotnet run --project src/WeChatVoice.Cli -- workspace materialize --snapshot-directory .\raw-snapshot --external-decryptor .\tools\decryptor.exe --output .\decrypted-db --workspace-output .\.wechatvoice\local-workspace.json --key-file .\key.bin
echo '{"requestId":"1","operation":"ping"}' | dotnet run --project src/WeChatVoice.ElevatedHelper
```

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
workspace. The built-in `weixin-windows-4` adapter identity is registered
centrally but is non-matching until a verified schema mapping is supplied.
Commands therefore fail clearly instead of guessing table mappings.
`workspace materialize` is a fixed external decryptor boundary: it passes only
`--input-root`, `--output-root`, and optional `--key-file`; it first verifies the
raw snapshot file set and hashes, requires every source database to be mapped,
then validates SQLite headers and `PRAGMA quick_check` on every output database.
It writes a materialization manifest and creates the local workspace JSON as one
closed workflow.

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

See [architecture.md](docs/architecture.md), [adr-0001-sqlite-runtime.md](docs/adr-0001-sqlite-runtime.md), [security.md](docs/security.md),
and [agent-handoff.md](docs/agent-handoff.md) before extending the project.
