# WeChatVoiceToolkit

A .NET 10, Windows-focused foundation for safely inspecting user-provided
WeChat data snapshots and exporting voice media through version-specific schema
adapters.

The repository now contains a live-validated, end-to-end path for the exact
signed Weixin Windows 4.1.11.55 build: restricted in-memory key acquisition,
ephemeral SQLCipher materialization, verified local workspace creation,
contact lookup, voice audit, and idempotent raw SILK export. No plaintext key
file is involved.

Two thin hosts share one workflow layer:

- `WeChatVoice.Cli` — the audited command-line surface below.
- `WeChatVoice.Desktop` — an Avalonia UI (normal privilege) with pages for
  environment, source snapshot, materialization, contacts, scan, export, and
  history/diagnostics. The UI only composes `WeChatVoice.Workflows`; it never
  opens SQLite, reads process memory, or touches a Key Broker implementation,
  and it never launches the CLI to do its work. Run it with
  `dotnet run --project src/WeChatVoice.Desktop`, or pass `--smoke-check` for a
  headless CI smoke.

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
dotnet run --project src/WeChatVoice.Cli -- workspace adopt --output .\decrypted-db --workspace-output .\.wechatvoice\local-workspace.json
dotnet run --project src/WeChatVoice.Cli -- materialization recover --output .\decrypted-db --workspace-output .\.wechatvoice\local-workspace.json
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
workflow. If the database commit succeeds but the workspace JSON write is
interrupted, `workspace adopt` / `materialization recover` revalidate the
state marker, manifest, output hashes, and workspace before completing the
commit; they never decrypt the databases again. Recoverable output is marked
`FailedRecoverable` when validation or adoption fails.

Export output is idempotent by `SourceStableKey` (adapter family, account,
conversation, message primary key, and media primary key); physical paths use
only a content hash fan-out and never the message timestamp. Snapshot and
database hashes remain provenance only. Missing identity or media association
is rejected before payload access. Runs are stored under
`runs/<run-id>.manifest.private.json`, the portable
`runs/<run-id>.dataset.manifest.json`, and an append-and-flush
`runs/<run-id>.jsonl` journal. The rolling products are explicitly split into
`manifest.private.json`, `dataset.manifest.json`, and `dataset.csv`.
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

Optional duration analysis reuses the existing SILK decoder boundary. Set
`WECHATVOICE_SILK_DECODER_PATH` to the reviewed decoder executable, then enable
the Desktop scan option “解码计算时长”. The scanner stages WAV output only in
the OS temporary directory, validates RIFF/PCM structure, computes duration
from PCM frames, and deletes the derived file. Normal scans and raw SILK export
never start the decoder.

For high-volume duration work, a reviewed decoder may expose the resident
`wechatvoice-decoder-jsonl-v1` protocol. Set
`WECHATVOICE_SILK_DECODER_WORKER_PATH` instead; the host keeps one controlled
worker process alive, sends one bounded JSONL request at a time, and exchanges
only temporary input/output paths. Worker stdout is protocol-only and stderr is
bounded. The worker executable must support `--worker --protocol
wechatvoice-decoder-jsonl-v1 --sample-rate 24000`.

For a complete self-contained `win-x64` layout, use the single package entry
point below. It publishes CLI, Desktop, Broker, Worker, native SQLCipher,
post-signature bundle manifests, package manifest, a pinned Microsoft SBOM Tool
SPDX document, checksums, and Desktop smoke verification in one path:

```powershell
dotnet restore WeChatVoice.slnx --locked-mode --runtime win-x64
./scripts/package-release.ps1
```

For formal distribution, sign that layout into a protected MSIX installer:

```powershell
./scripts/package-msix.ps1 -PublishDirectory artifacts/package `
  -OutputPath artifacts/WeChatVoiceToolkit-win-x64.msix `
  -PfxPath $env:WECHATVOICE_SIGNING_PFX_PATH `
  -PfxPassword $env:WECHATVOICE_SIGNING_PFX_PASSWORD
```

The ZIP layout is retained only as a portable diagnostic attachment. It is not
a formal Broker distribution because an ordinary user can extract it into a
writable directory. Do not use a single-project `dotnet publish` as a release
package.

Formal MSIX lifecycle commands are also provided for operators and release
automation:

```powershell
./scripts/install-msix.ps1 -PackagePath artifacts/WeChatVoiceToolkit-win-x64.msix `
  -UpdateManifestPath artifacts/WeChatVoiceToolkit-win-x64.update-manifest.json `
  -RunTrustSmoke
./scripts/rollback-msix.ps1 -PackagePath artifacts/rollback/WeChatVoiceToolkit-win-x64.msix -RunTrustSmoke
./scripts/uninstall-msix.ps1
```

Upgrade and rollback never remove Snapshot, Workspace, or Export data. The
update manifest binds the package filename, identity name/publisher,
PublisherId/PackageFamilyName, architecture, executable, version, length, and
SHA-256 before installation; the installer also requires the independently
pinned release certificate thumbprint and public-key ID.

Formal packages use the fixed `WeChatVoiceToolkit` / `x64` /
`WeChatVoice.Desktop.exe` AppX identity. Publisher trust is supplied by the
protected release policy and is never inferred from a package supplied by a
caller.

Curated training datasets can be checked or repaired without changing the
original export SILK files:

```powershell
wechatvoice dataset verify --export <export-root> --output <dataset-root>
wechatvoice dataset repair --export <export-root> --output <dataset-root>
```
