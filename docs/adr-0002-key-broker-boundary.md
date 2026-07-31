# ADR 0002: One-shot Key Broker boundary

## Status

Accepted for route two groundwork. No key-extraction or database-encryption
profile is enabled yet.

## Decision

`WeChatVoice.ElevatedHelper` remains a metadata-only diagnostic service. It
must never gain process-memory, key, or materialization operations.

Route two uses the separate `WeChatVoice.KeyBroker.exe` with a UAC
`requireAdministrator` manifest. The broker is one-shot rather than a JSONL
service. Its request admits only:

- protocol version;
- request ID and nonce;
- content-addressed Snapshot ID;
- `.wechatvoice/snapshot-manifest.json` path;
- `acquire-and-materialize` operation.

PID, process name, address, read length, module base, database path, backend
executable, arbitrary arguments, and output commands are rejected. Before any
future Profile selection, the broker rebinds the manifest to its containing
snapshot directory and verifies its complete file set, lengths, hashes, and
Snapshot ID. Errors are structured and contain no free-form backend output.

Until a version-specific key-extraction Profile and database-encryption Profile
have passed fixture tests, the broker returns `profile_unavailable`. It never
returns raw keys and the CLI has no plaintext `--key-file` option.

Formal materialization backends are registry entries with version and expected
binary identity. The external backend is development-only and requires an
explicit opt-in. It has a bounded execution time, kills its process tree on
timeout/cancellation, redacts diagnostic output, and accepts only an explicit
source-to-output manifest.

## Current identity evidence

Read-only inspection of the currently running installation on 2026-08-01
observed:

- Weixin product/file version `4.1.11.55`;
- Weixin.exe SHA-256
  `ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d`;
- a valid Authenticode signature whose subject is Tencent Technology
  (Shenzhen) Company Limited.

This is process identity evidence only. It is not sufficient to infer memory
layout, key location, KDF, page cipher, HMAC, WAL rules, or database schema, and
therefore does not enable a Profile.

## Next evidence required

The next route-two implementation needs a verified raw Snapshot for the same
account/build plus reproducible encryption fixtures. Only then can a precise
build-range Profile define process identity policy, bounded candidate location,
candidate validation against the encrypted first page, full materialization,
and `PRAGMA quick_check` acceptance.
