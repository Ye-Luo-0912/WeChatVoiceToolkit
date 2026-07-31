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

Do not implement a schema adapter, UI, key extraction, or decryption by
inference. Extend exact Profiles through the registry while preserving the
no-arbitrary-process/no-write/no-key-output floor. The real databases have
non-SQLite first pages. Their message/media filename numbers are not one-to-one,
so Probe reports topology differences as informational and leaves association
to a verified Adapter. Continue only after stable evidence supports a
version-specific key/decryption profile. The old
plaintext key-file placeholder is removed; development backends require an
explicit untrusted flag and an explicit source-to-output manifest.

Route-two groundwork is recorded in `adr-0002-key-broker-boundary.md`.
ADR 0003 records the ephemeral flow. The CLI now creates the random one-time
pipe and launches the installed Broker; the Broker verifies the reserved
Snapshot Manifest and content-addressed Snapshot ID before returning
`profile_unavailable` while the Profile registry is empty.
The separate login `key_info.db` is ordinary SQLite, but only its schema and
field-length distribution have been inspected; no field values were read and
its 180-byte BLOB must not be treated as a key without validation. Stable
business and login snapshots now exist only as ignored local evidence. The 21
business databases have 21 distinct first-page salts, so key results must be
validated and bound per database group rather than assumed global.

`WeixinWindows4SqlCipherKeyValidator` implements only constant-time first-page
HMAC candidate validation and clears its derived buffers. The non-registered
`WeixinWindows41155Profile` now combines that validator with the bounded
candidate scanner and requires every verified database group to authenticate;
its Fake tests cover success and partial-group failure. It is deliberately not
in the formal registry until a real plaintext materializer exists. Do not adopt
the reviewed upstream scanner's whole-region reads or its key/PID/address
logging.

`IEphemeralDatabaseMaterializer` now declares `BackendId` and
`EncryptionProfileId`. `VerifiedKeyAcquisition` rejects duplicate or
cross-Snapshot/cross-Profile bindings, and the orchestration service validates
the returned materialization provenance before releasing the result. The
available `sqlite3.exe` is ordinary SQLite without SQLCipher codec support, so
do not wire it to the encrypted business databases.

`DatabaseMaterializerTests` use a real fake child process and cover success,
missing/extra/invalid/duplicate/unknown mappings, sensitive output redaction,
binary hash mismatch, timeout, and cancellation. The next blocker is a bounded
candidate-location fixture for signed Weixin 4.1.11.55, followed by multi-group
validation, full DB/WAL materialization, and `PRAGMA quick_check`.
