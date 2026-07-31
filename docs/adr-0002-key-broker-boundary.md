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

Read-only inspection first used a live-source copy, then repeated the inventory
after Weixin fully exited. The stable business and login snapshots both passed
the group-level inventory and content-hash checks on their first attempt with
`potentiallyInconsistent = false`. They established a small amount of format
evidence without reading or recording login field values:

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

The stable business snapshot contains 21 discovered `.db` files and 21 distinct
first-page salts. Four observed `*.db-first.material` files begin with the same
16 bytes as their corresponding database. This proves neither one key per salt
nor one shared key; a future acquisition result must validate and bind each
database group independently.

This evidence does not establish the login BLOB encoding, prove that it contains
database key material, or bind it to any database group. Code must not infer
those semantics.

## Candidate-validation evidence

The Apache-2.0 implementation in
[`huohuoer/wechat-cli` at `a3789232`](https://github.com/huohuoer/wechat-cli/blob/a3789232d4f79bf0b30634d9dadbce71e4acd601/wechat_cli/keys/common.py)
validates a 32-byte candidate against a 4096-byte encrypted first page with the
page HMAC-SHA512 rather than a plaintext-header coincidence. Its scanner is not
adopted: it reads whole memory regions and logs keys, PIDs, and addresses.

`WeixinWindows4SqlCipherKeyValidator` implements only the non-logging validation
primitive. It uses constant-time comparison, clears derived key and temporary
page buffers, and has fixed synthetic positive, wrong-key, tamper, and shape
vectors. It does not acquire a key, decrypt a page, register a build Profile, or
change the broker's fail-closed behavior.

## Next evidence required

The next route-two implementation needs a bounded candidate-location fixture for
the exact signed build. After a candidate validates independently against the
required message, media, and contact groups, the Profile must still pass full
DB/WAL materialization and `PRAGMA quick_check` before it can be registered.
