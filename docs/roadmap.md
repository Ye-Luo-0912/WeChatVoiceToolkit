# Roadmap

## Completed foundation

1. Group-level snapshots now have content-addressed IDs and exclude only the
   reserved `.wechatvoice/` metadata directory.
2. Shareable probes and executable workspaces are separate. Workspace loads
   require `ILocalWorkspaceVerifier`; adapters receive only a verified local
   workspace.
3. Materialization has a verified raw-snapshot boundary, source-to-output
   database mapping, strict output traversal, SQLite acceptance checks, and a
   persisted materialization manifest. The CLI closes the path into a local
   workspace JSON.
4. Export paths use only `SourceStableKey`, catalogs are disposable, and run
   Journals use a Run Lease with processing/failure/cancellation and manifest
   commit events. `voice export recover --journal` rebuilds a manifest after a
   crash.
5. Route two now has a separate one-shot UAC Key Broker, fixed request protocol,
   raw Snapshot verification before Profile selection, registered
   materialization backends, bounded backend execution, and a real fake-process
   integration suite for output mapping and failure handling.

## Current blocker and next step

The actual WeChat 4.x files observed on the development machine are encrypted
or proprietary containers, not ordinary SQLite. Do not guess tables or keys.
The next implementation step requires a verified version-specific key and
database-encryption profile plus schema evidence for `message_N.db`,
`media_N.db`, and the contact database. The privileged boundary is the
one-shot `WeChatVoice.KeyBroker.exe`; no plaintext key-file interface is
supported. Until that profile exists, the broker and formal materialization
backend fail closed. Then implement one isolated adapter for exact contact
selection, incoming voice association, and raw SILK output.

The currently observed signed Weixin build is 4.1.11.55, but no verified raw
Snapshot or encryption fixture is present in the workspace. Process version
evidence alone must not be promoted into a key-extraction Profile.

WAV decoding remains a derived, later phase and must not become a prerequisite
for the first usable SILK chain.
