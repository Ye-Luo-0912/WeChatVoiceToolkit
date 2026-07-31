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

## Next product work

1. Split the oversized CLI composition file into command and service classes
   without changing verified command behavior.
2. Add a guided command that composes snapshot selection, materialization,
   contact selection, scan confirmation, and export while retaining the same
   trust boundaries.
3. Recover voice duration from verified message metadata, if evidence supports
   it, so scan and training-quality manifests can report duration accurately.
4. Add packaged/self-contained win-x64 smoke tests and an installer that places
   the Broker/Worker bundle in a normal-user non-writable directory.
5. After raw SILK remains stable, add a batch or resident decoder worker and
   strict RIFF/PCM validation. WAV/RVC work remains a derived later phase.

New Weixin versions require a new exact process/module Profile and schema
evidence. There is no unknown-version heuristic fallback.
