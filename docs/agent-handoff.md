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
decryption by inference. The real WeChat files observed so far are encrypted
or proprietary. Continue only after the user provides verified schema data,
version context, and a version-specific key/decryption profile. The old
plaintext key-file placeholder is removed; development backends require an
explicit untrusted flag and an explicit source-to-output manifest.

Route-two groundwork is recorded in `adr-0002-key-broker-boundary.md`.
`WeChatVoice.KeyBroker` now verifies the reserved Snapshot Manifest and
content-addressed Snapshot ID before returning `profile_unavailable`.
`DatabaseMaterializerTests` use a real fake child process and cover success,
missing/extra/invalid/duplicate/unknown mappings, sensitive output redaction,
binary hash mismatch, timeout, and cancellation. The next blocker is evidence,
not another generic abstraction: obtain a verified Snapshot and encryption
fixtures for the observed signed Weixin 4.1.11.55 build.
