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
   materialization backends, bounded backend execution, a guarded
   experimental Profile-driven key acquisition service, and a fixed SQLCipher
   Worker that completes synthetic encrypted-fixture materialization into a
   Local Workspace. The Broker enforces launched-PID binding, private Snapshot
   staging, caller budgets, and explicit experimental-profile opt-in. It
   identifies the unique current-session Weixin root and may inspect only its
   same-image descendants under the same exact identity policy.
6. Stable business/login snapshots for the observed build pass group-level
   verification. A candidate-only first-page HMAC validator has fixed synthetic
   vectors, zeroes temporary key material, and remains outside the Profile
   registry.
7. The formal CLI owns the one-command UAC Broker flow. The Broker uses a
   one-time current-user pipe, fixed four-field request, read-only process
   foundations, exact identity policies, bounded overlapping scans, and
   deterministic key disposal. CI uses Fakes and never reads a real process.

## Current blocker and next step

The actual WeChat 4.x business database first pages observed on the development
machine are encrypted or proprietary rather than ordinary SQLite. The separate
login `key_info.db` is ordinary SQLite, but its BLOB semantics remain unverified.
Do not guess tables, BLOB formats, or keys. Message and media filename shards
are also not one-to-one; only an Adapter with verified schema evidence may
resolve their relationship.
The first controlled live attempt verified the unique current-session Weixin
root and its same-image descendants, staged the stable 21-database Snapshot,
scanned the bounded process-tree budget, and found zero candidates in the
current ASCII key form. Materialization stopped before Worker output. The next step is
evidence-backed analysis of the exact page/key format; do not weaken validation
or guess a new format. The Worker is currently proven only with synthetic
SQLCipher fixtures and must not be treated as a Weixin decryptor until page
format, KDF/HMAC, and WAL behavior match. After that evidence, implement one
isolated adapter for exact contact selection, incoming voice association, and
raw SILK output. No plaintext key-file interface is supported on the formal
path.

The currently observed signed Weixin build is 4.1.11.55. Stable raw business and
login snapshots exist only in the ignored local workspace; they must never be
committed. The 21 business databases have distinct first-page salts. Process
version, salt, or login-metadata evidence alone must not be promoted into a
key-extraction Profile. The formal backend remains experimental until a candidate
also passes full DB/WAL materialization and `PRAGMA quick_check` against the
real signed build.

WAV decoding remains a derived, later phase and must not become a prerequisite
for the first usable SILK chain.
