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
6. Stable business/login snapshots for the observed build pass group-level
   verification. A candidate-only first-page HMAC validator has fixed synthetic
   vectors, zeroes temporary key material, and remains outside the Profile
   registry.

## Current blocker and next step

The actual WeChat 4.x business database first pages observed on the development
machine are encrypted or proprietary rather than ordinary SQLite. The separate
login `key_info.db` is ordinary SQLite, but its BLOB semantics remain unverified.
Do not guess tables, BLOB formats, or keys. Message and media filename shards
are also not one-to-one; only an Adapter with verified schema evidence may
resolve their relationship.
The next implementation step requires a bounded candidate-location fixture for
the exact signed build, then validation against the required `message_N.db`,
`media_N.db`, and contact groups. The privileged boundary is the
one-shot `WeChatVoice.KeyBroker.exe`; no plaintext key-file interface is
supported. Until that profile exists, the broker and formal materialization
backend fail closed. Then implement one isolated adapter for exact contact
selection, incoming voice association, and raw SILK output.

The currently observed signed Weixin build is 4.1.11.55. Stable raw business and
login snapshots exist only in the ignored local workspace; they must never be
committed. The 21 business databases have distinct first-page salts. Process
version, salt, or login-metadata evidence alone must not be promoted into a
key-extraction Profile. The formal backend remains unavailable until a candidate
also passes full DB/WAL materialization and `PRAGMA quick_check`.

WAV decoding remains a derived, later phase and must not become a prerequisite
for the first usable SILK chain.
