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

## Desktop normal flow

The Desktop source-snapshot page automatically searches the supported Weixin
data roots when the page is first opened. If exactly one complete, selectable
account is found, it is selected and a private snapshot destination is created
under `%LocalAppData%\WeChatVoiceToolkit\Data\Snapshots\` using an opaque
account fingerprint. The normal user does not need to find or understand
`db_storage`, or choose a snapshot directory.

When multiple accounts are found, the Desktop leaves the selection empty until
the user explicitly chooses an account. If discovery finds no usable account,
the page offers a bounded recheck and a validated manual folder picker as a
fallback. A truncated discovery is shown as potentially incomplete rather than
being treated as a complete search. Weixin must be fully closed before the
stable snapshot action is enabled; after the snapshot completes, the user may
reopen Weixin.

The guided Desktop sequence is:

`Environment assessment -> automatic account discovery -> explicit account
choice when needed -> exit Weixin -> automatic stable snapshot -> reopen
Weixin -> materialize -> contact -> incoming scan -> raw SILK export`.

After a snapshot completes, the materialization page automatically fills the
verified snapshot path, an opaque Workspace output directory, and its
Workspace JSON path. The user only needs to start materialization and confirm
the account/UAC prompt; paths are still available under the page details for
diagnostics.

## Resume / 继续上次工作

The Desktop now opens on a **resume-first** page. On activation it inspects the
recently used workspaces through the shared `IProjectStateWorkflow`
(`WeChatVoice.Workflows`) and classifies each one as `ValidReusable`,
`Recoverable`, `Stale`, `Invalid`, `Busy`, or `Missing`. The UI only presents
the classification and the user's continue choice; it never re-implements the
verified reuse/recover decision.

Continuing a project never repeats the expensive main chain:

- no re-snapshot, no re-materialization, no re-UAC / key acquisition;
- a verified workspace JSON is reloaded and reused (`ValidReusable`);
- a recoverable materialization is adopted without re-decrypting; a lost or
  corrupt Workspace JSON is repaired against the completed materialized root;
- the existing canonical Workspace output directory is reused instead of
  allocating a new GUID copy.

The shared `ProjectStageState`/`ProjectStageStatus` models and the
`ProjectStateWorkflow` (inspect + resume) live in `WeChatVoice.Workflows` so
both the Desktop and CLI hosts share one authoritative path. Second-run reuse
is covered by workflow integration tests and Desktop/Avalonia-Headless tests.

Choose **从微信数据源刷新** only when you want to re-check the Weixin source and
create a new snapshot; otherwise the app reuses verified local state.

Because "refresh" can mean several different workflows, the resume page also
renders five distinct **refresh actions** (`WeChatVoice.Core.Models.RefreshActionCatalog`)
so the user never confuses "continue" with "re-run everything":

| 动作 | 复用 | 重新执行 |
|---|---|---|
| 继续现有项目 | 已验证快照、工作区、扫描、已导出 SILK | 无（快照/解密/UAC/导出都不重跑） |
| 从微信数据源刷新 | 未变化的已验证状态 | 检测源变化；必要时新快照与解密 |
| 重新扫描当前工作区 | 快照、解密、账户确认 | 语音查询与扫描 |
| 重新分析音频（时长/质量） | 已导出 SILK 与有效缓存 | 未知/过期音频的时长与质量分析 |
| 重建训练数据集 | 原始 SILK 导出与选择 profile | 数据集构建（SILK→WAV 派生产物） |

Each action routes to the page that owns that workflow via a lightweight
`INavigationService`; the `IProjectStateWorkflow` verify/reuse decision stays
authoritative.

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
dotnet run --project src/WeChatVoice.Cli -- voice export verify --output .\exports\peer
dotnet run --project src/WeChatVoice.Cli -- voice export repair --output .\exports\peer
dotnet run --project src/WeChatVoice.Cli -- voice export run-retention preview --output .\exports\peer --keep-recent 5
dotnet run --project src/WeChatVoice.Cli -- voice export run-retention compact --output .\exports\peer --keep-recent 5
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

Duration analysis and audio preview use the bundled WeChat SILK v3 decoder automatically
when the packaged `WeChatVoice.SilkDecoder.exe` is present. The decoder is a
fixed, local process and does not require FFmpeg or a separate installation.
Its upstream MIT license and pinned SHA-256 are recorded beside the bundled
asset in `src/WeChatVoice.Workflows/Resources/THIRD_PARTY_LICENSES.md`.

An optional reviewed decoder can still override the bundled decoder. Set
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
wechatvoice-decoder-jsonl-v1 --sample-rate 48000`.

Scan results are persisted and reused across restarts: a `ScanCacheService`
binds each prepared selection to the verified workspace identity and the query
fingerprint (catalog + query + selection-engine + duration-resolver), so a
later scan of the unchanged workspace reuses the cached result instead of
re-reading the catalog. The cache lives under the managed
`Data/scan-cache` directory and is integrity-checked on read; a changed query
fingerprint or a verification failure triggers a fresh scan that is written
back to the cache.

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

./scripts/generate-appinstaller.ps1 `
  -PackagePath artifacts/WeChatVoiceToolkit-win-x64.msix `
  -OutputPath artifacts/WeChatVoiceToolkit.appinstaller `
  -PackageUri https://example.invalid/releases/latest/download/WeChatVoiceToolkit-win-x64.msix `
  -AppInstallerUri https://example.invalid/releases/latest/download/WeChatVoiceToolkit.appinstaller
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

终端用户更新使用发布附件中的 `WeChatVoiceToolkit.appinstaller`。它通过
HTTPS 固定 AppX Name、Publisher、x64 Version、Desktop executable 和 MSIX
地址，由 Windows App Installer 按同一 Package Identity 执行升级；终端用户
不需要配置 Publisher 环境变量或运行安装脚本。

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

Dataset curation is a single player-style workbench in the Desktop "数据集整理"
page. It automatically reuses the last export and selection profile; the normal
path is preview, select, and “一键生成训练集”. The default output is validated
 mono PCM WAV at 48 kHz / 16-bit (about 768 kbps). If FFmpeg is discoverable in
PATH or the user's WinGet FFmpeg package, it is used after decoding with fixed
arguments to normalize the WAV; the source SILK is never modified. Verify and
repair remain available for the derived training set.
