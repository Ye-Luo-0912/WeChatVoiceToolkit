# Roadmap

## Completed main path

1. Stable, group-validated snapshots use content-addressed IDs and preserve all
   source files outside the reserved `.wechatvoice/` metadata directory.
2. Shareable probes, verified raw snapshots, verified materializations, and
   executable local workspaces are separate trust boundaries.
3. The one-shot elevated Key Broker verifies the current-user Weixin process
   tree, exact 4.1.11.55 executable identity, and exact versioned WCDB module.
   It accepts no caller-selected PID, address, length, command, or raw-key
   output.
4. The 4.1.11.55 Profile recognizes the observed protected WCDB key specs,
   validates candidates using exact SQLCipher page profiles, binds keys to
   database-group fingerprints, and clears sensitive buffers.
5. The fixed SQLCipher Worker materializes 20 required databases into ordinary
   SQLite. Every output passes header, schema, hash, and `PRAGMA quick_check`
   acceptance. The migration-only `migrate/unspportmsg.db` is the sole narrow
   intentionally-ignored source status.
6. Materialization automatically emits a verified local workspace containing a
   stable account identity; no plaintext key file is created.
7. The exact Weixin 4.x Adapter supports contact list/search, direction-aware
   voice scanning, strict message/media association, streaming BLOB reads, and
   raw SILK export.
8. Export uses stable source keys, content-addressed paths, per-run flushed
   Journals, committed Manifests, hash verification, and repeat-run safe skips.
   Each source BLOB is read once; run manifests inherit full materialization
   provenance.
9. Broker and worker binaries are trusted through split Development/Release
   policies: release requires Authenticode + pinned publisher + hash-bound
   publish manifests + a non-user-writable install directory, and the publish
   smoke signs or fails closed. Private Broker staging is DACL-restricted to
   SYSTEM and Administrators.
10. Account identity is a confirmed candidate, never a silent path pick: the
    detected account must be explicitly confirmed (`--account` or the
    `IAccountConfirmation` port) before privileged materialization.
11. Stable error codes (`ErrorCode` + `ErrorCatalog`) cross the Broker/CLI
    boundary with `IsRetryable`/`SuggestedAction`/`NonSensitiveTechnicalContext`;
    presentation layers own localized text.
12. Workspace verification builds one `VerifiedFileIndex` shared by dataset
    probing, provenance verification, and the adapter; maximum results merge
    globally across message shards, and Name2Id reads stay bounded to the
    resolved conversation.
13. The Desktop guided flow carries one `ExportProjectSession`, requires an
    explicit contact selection, fixes the first-pass direction to incoming,
    binds Scan and Export to an immutable result-set fingerprint, and serializes
    high-cost operations through an application coordinator.
14. The Desktop supports source discovery, recent Workspace verification and
    recovery, manifest-checked materialized Workspace deletion, typed trust
    preflight, awaitable UI dispatch, and headless page construction tests.
15. Release packaging has one complete-layout entry point with RID-locked
   restore, post-signature bundle manifests, recursive package closure, SBOM,
   checksums, and protected-directory trust smoke.
16. The Desktop flow now keeps Workspace JSON paths in the project session,
    invalidates Scan/Export plans when the source, Workspace, contact, or query
    changes, and forwards the immutable maximum-result limit through Export.
17. Workspace catalogs hold read-only database file leases for their lifetime;
    content verification is cached per lease and payload reads perform a cheap
    identity/metadata recheck. Existing export artifacts are still hashed before
    a repeat-run skip; the artifact index is bookkeeping only.
18. Materialization commit markers are monotonic and cross-process locked;
   cancellation uses the Broker pipe-disconnect protocol, and Broker/Worker
   trust failures are typed before reaching Desktop presentation.
19. The exact 4.1.11.55 adapter re-derives account self-identity evidence from
   the verified `contact.encrypt_username = username` row on every catalog
   open; persisted user confirmation never upgrades technical evidence.
20. Materialization page inputs follow the application session when a new
   snapshot is created, and state-transition retries validate their declared
   predecessor set even when the requested state is already durable.

## Next product work

1. Split the oversized CLI composition file into command and service classes
   without changing verified command behavior.
2. Recover voice duration from a verified message metadata field only if the
   user supplies schema evidence and test data for that field. Until then,
   `--resolve-durations` and the Desktop option use the reviewed external SILK
   decoder boundary and report unknown duration when it is not configured or
   cannot decode.
3. Add a batch or resident decoder worker only after the raw SILK path is
   stable; keep strict RIFF/PCM validation and never overwrite SILK.
4. Add a real installer only after an installer format, update channel, and
   certificate distribution policy are selected. The complete ZIP package is
   the current distribution artifact; no installer is implied.

New Weixin versions require a new exact process/module Profile and schema
evidence. There is no unknown-version heuristic fallback.
