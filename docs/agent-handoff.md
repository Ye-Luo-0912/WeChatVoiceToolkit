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

Do not implement a schema adapter, UI, key scanning, key extraction, or
decryption by inference. The real business databases observed so far have
non-SQLite first pages. Their message/media filename numbers are not one-to-one,
so Probe reports topology differences as informational and leaves association
to a verified Adapter. Continue only after stable evidence supports a
version-specific key/decryption profile. The old
plaintext key-file placeholder is removed; development backends require an
explicit untrusted flag and an explicit source-to-output manifest.

Route-two groundwork is recorded in `adr-0002-key-broker-boundary.md`.
`WeChatVoice.KeyBroker` now verifies the reserved Snapshot Manifest and
content-addressed Snapshot ID before returning `profile_unavailable`.
The separate login `key_info.db` is ordinary SQLite, but only its schema and
field-length distribution have been inspected; no field values were read and
its 180-byte BLOB must not be treated as a key without validation. Stable
business and login snapshots now exist only as ignored local evidence. The 21
business databases have 21 distinct first-page salts, so key results must be
validated and bound per database group rather than assumed global.

`WeixinWindows4SqlCipherKeyValidator` implements only constant-time first-page
HMAC candidate validation and clears its derived buffers. Its synthetic vectors
do not enable a Profile. Do not adopt the reviewed upstream scanner's whole-
region reads or its key/PID/address logging.

`DatabaseMaterializerTests` use a real fake child process and cover success,
missing/extra/invalid/duplicate/unknown mappings, sensitive output redaction,
binary hash mismatch, timeout, and cancellation. The next blocker is a bounded
candidate-location fixture for signed Weixin 4.1.11.55, followed by multi-group
validation, full DB/WAL materialization, and `PRAGMA quick_check`.
