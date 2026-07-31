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
dotnet run --project src/WeChatVoice.Cli -- dataset probe --root .\decrypted-db --output .\dataset-probe.json
dotnet run --project src/WeChatVoice.Cli -- contact list --dataset .\dataset-probe.json
dotnet run --project src/WeChatVoice.Cli -- contact search --dataset .\dataset-probe.json --query wxid
dotnet run --project src/WeChatVoice.Cli -- voice scan --dataset .\dataset-probe.json --contact-username wxid_xxx --direction incoming --from 2025-01-01
dotnet run --project src/WeChatVoice.Cli -- voice export --dataset .\dataset-probe.json --contact-username wxid_xxx --direction incoming --format silk --output .\exports\peer
echo '{"requestId":"1","operation":"ping"}' | dotnet run --project src/WeChatVoice.ElevatedHelper
```

Snapshots require recognized WeChat processes to be closed by default. The
explicit `--allow-live-source` opt-in marks the internal
`.wechatvoice/snapshot-manifest.json` as `potentiallyInconsistent`; the source
root `snapshot.json` name is reserved and excluded. Snapshot acceptance is
group-level: the complete file set, length, modification time, and file
identity must match before and after an attempt.

`voice export` is now a real application path, but this foundation build does
not register a verified WeChat data-set adapter yet. Dataset probing and schema
fingerprints are available; contact/scan/export commands fail clearly until an
adapter for the inspected message/media/contact schemas is registered rather
than guessing table mappings. Probe output redacts absolute paths by default;
use `--include-local-paths` only for a local-only workflow.

Export output is idempotent by stable data-set key and source SHA-256. Runs are
stored under `runs/<run-id>.manifest.json` and `runs/<run-id>.jsonl`; only
`latest.manifest.json` is replaced.

See [architecture.md](docs/architecture.md), [security.md](docs/security.md),
and [agent-handoff.md](docs/agent-handoff.md) before extending the project.
