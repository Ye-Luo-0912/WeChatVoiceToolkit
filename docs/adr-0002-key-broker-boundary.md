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

Read-only inspection of a live-source copy from that build also established a
small amount of format evidence without reading or recording field values:

- the message, media, and contact database first pages are not ordinary SQLite
  headers, while their WAL files use the standard WAL magic and a 4096-byte
  page size;
- the observed bundle has multiple message files but only one numbered media
  file, so filename shard parity is not a valid completeness rule;
- the separate login `key_info.db` is ordinary SQLite with a single
  `LoginKeyInfoTable` containing `user_name_md5`, `key_md5`, `key_info_md5`, and
  `key_info_data` columns;
- all 52 observed rows had 32-character username/info digest fields, an empty
  `key_md5` field, and a 180-byte BLOB. No digest or BLOB value was read or
  logged.

This evidence does not establish the BLOB encoding, prove that it contains
database key material, or bind it to any database group. Code must not infer
those semantics. The live-source copy is explicitly potentially inconsistent
and is not a Profile fixture.

## Next evidence required

The next route-two implementation needs a stable raw Snapshot made after
Weixin exits, for the same account/build, plus reproducible encryption fixtures.
Only then can a precise
build-range Profile define process identity policy, bounded candidate location,
candidate validation against the encrypted first page, full materialization,
and `PRAGMA quick_check` acceptance.
