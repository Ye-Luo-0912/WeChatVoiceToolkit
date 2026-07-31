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
version context, and either a key-file or fixed decryptor.
