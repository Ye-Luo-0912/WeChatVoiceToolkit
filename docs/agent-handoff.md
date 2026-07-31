# Current handoff

The safety foundation is implemented and pushed on `main`. Before consuming a
workspace, use `workspace verify`; before materialization, verify the raw
snapshot so the pipeline is `VerifiedRawSnapshot -> VerifiedMaterialization ->
VerifiedLocalWorkspace`. `IVoiceCatalog` is `IAsyncDisposable`, and CLI callers
must use `await using`.

Export uses stable-key paths, independent original/decoded states, WAV checks
for untyped existing decoded artifacts, and a Run Lease. Journal events are
`run-started`, item events, `processing-completed`/`run-cancelled`/`run-failed`,
and `manifest-committed`; a truncated final JSONL line is ignored during
recovery. Use `voice export recover --journal <runs/id.jsonl>` after a crash.

Do not implement a schema adapter, UI, or decryption by inference. The guarded
4.1.11.55 Profile is experimental and must remain behind explicit opt-in until
real database validation is complete. Extend exact Profiles through the
registry while preserving the no-arbitrary-process/no-write/no-key-output floor. The real databases have
non-SQLite first pages. Their message/media filename numbers are not one-to-one,
so Probe reports topology differences as informational and leaves association
to a verified Adapter. Continue only after stable evidence supports a
version-specific key/decryption profile. The old
plaintext key-file placeholder is removed; development backends require an
explicit untrusted flag and an explicit source-to-output manifest.

Route-two groundwork is recorded in `adr-0002-key-broker-boundary.md`.
ADR 0003 records the ephemeral flow and ADR 0004 records the fixed SQLCipher
Worker boundary. The CLI creates the random one-time pipe and launches the
installed Broker; the Broker binds the pipe to the launched server PID, stages
the verified Snapshot privately, then composes the guarded experimental
Profile, group-bound acquisition service, SQLCipher Worker, and Local
Workspace creation. It still returns `profile_unavailable` when no exact live
process or database evidence matches, and refuses the Profile without explicit
experimental opt-in.
The separate login `key_info.db` is ordinary SQLite, but only its schema and
field-length distribution have been inspected; no field values were read and
its 180-byte BLOB must not be treated as a key without validation. Stable
business and login snapshots now exist only as ignored local evidence. The 21
business databases have 21 distinct first-page salts, so key results must be
validated and bound per database group rather than assumed global.

`WeixinWindows4SqlCipherKeyValidator` implements constant-time first-page HMAC
candidate validation and clears its derived buffers. The experimental
`WeixinWindows41155Profile` now combines that validator with the bounded
candidate scanner and requires every verified database group to authenticate;
its Fake tests cover success and partial-group failure. The Profile is wired
only to the guarded Broker path and remains uncertified until the real Weixin
database format matches. Do not adopt the reviewed upstream scanner's
whole-region reads or its key/PID/address logging.

`IEphemeralDatabaseMaterializer` now declares `BackendId` and
`EncryptionProfileId`. `VerifiedKeyAcquisition` rejects duplicate or
cross-Snapshot/cross-Profile bindings, and the orchestration service validates
the returned materialization provenance before releasing the result. The
available `sqlite3.exe` is ordinary SQLite without SQLCipher codec support, so
do not wire it to the encrypted business databases.

`DatabaseMaterializerTests` use a real fake child process and cover success,
missing/extra/invalid/duplicate/unknown mappings, sensitive output redaction,
binary hash mismatch, timeout, and cancellation. The first controlled live
attempt on 2026-08-01 found one matching signed Weixin process and verified the
stable 21-database Snapshot. The Broker pipe, identity check, private staging,
and 768 MiB scan all completed, but the experimental ASCII candidate scanner
found zero candidates and therefore stopped before any Worker output. This is
evidence that the current Profile assumptions do not match the live format; do
not weaken validation or guess a new key format. The next blocker is an
evidence-backed Profile update, followed by the first schema adapter and
multi-group incoming SILK association. The SQLCipher Worker already has
synthetic multi-step coverage for DB/WAL staging, quick-check, wrong-key
rejection, protocol rejection, and destination safety.
