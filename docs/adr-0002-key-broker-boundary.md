# ADR 0002: One-shot Key Broker boundary

## Status

Accepted for route two. The observed 4.1.11.55 extraction Profile is
`ExperimentalLive` and requires explicit CLI opt-in; no Profile is certified.

## Decision

`WeChatVoice.ElevatedHelper` remains a metadata-only diagnostic service. It
must never gain process-memory, key, or materialization operations.

Route two uses the separate `WeChatVoice.KeyBroker.exe` with a UAC
`requireAdministrator` manifest. The broker is one-shot rather than a JSONL
service. Its request admits only:

- protocol version;
- request ID;
- content-addressed Snapshot ID;
- `acquire-and-materialize` operation.

The 256-bit nonce is a transport bootstrap token embedded in the one-time pipe
name, not a request field. The installed CLI supplies the reserved Snapshot
Manifest and output paths as bootstrap arguments to the fixed Broker executable;
they are not protocol extensions.

PID, process name, address, read length, module base, database path, backend
executable, arbitrary arguments, and output commands are rejected. Before any
future Profile selection, the broker rebinds the manifest to its containing
snapshot directory and verifies its complete file set, lengths, hashes, and
Snapshot ID. Errors are structured and contain no free-form backend output.

The Broker currently composes the version-specific 4.1.11.55 extraction Profile
with the SQLCipher database-encryption backend only when
`--allow-experimental-profile` is present. It never returns raw keys and the CLI
has no plaintext `--key-file` option. The chain remains experimental until it
has been validated against the user's real encrypted database groups.

Formal materialization backends are registry entries with version and expected
binary identity. The external backend is development-only and requires an
explicit opt-in. It has a bounded execution time, kills its process tree on
timeout/cancellation, redacts diagnostic output, and accepts only an explicit
source-to-output manifest.

Profiles and database formats are intentionally extensible through reviewed
registries. Only dangerous capabilities are fixed: a Profile cannot introduce
caller-selected process-memory coordinates, writable process access, arbitrary
privileged commands, raw-key output, or an unknown-version fallback.

## Current identity evidence

Read-only inspection of the currently running installation on 2026-08-01
observed:

- Weixin product/file version `4.1.11.55`;
- Weixin.exe SHA-256
  `ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d`;
- a valid Authenticode signature whose subject is Tencent Technology
  (Shenzhen) Company Limited.

This is process identity evidence only. It is not sufficient to infer memory
layout, key location, KDF, page cipher, HMAC, WAL rules, or database schema. It
binds the experimental Profile's process descriptor but does not certify the
Profile's database assumptions.

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

`WeixinWindows4SqlCipherKeyValidator` implements the non-logging validation
primitive. It uses constant-time comparison, clears derived key and temporary
page buffers, and has fixed synthetic positive, wrong-key, tamper, and shape
vectors. The experimental Profile combines it with the bounded scanner and
group-bound acquisition service; it still does not imply that the observed
Weixin databases use the same cipher parameters.

## Next evidence required

The first controlled live attempt on 2026-08-01 matched one signed process and
scanned 768 MiB with zero candidates in the experimental ASCII form. No Worker
was invoked and no key material was emitted. The next route-two implementation
needs an evidence-backed candidate-location fixture and page-format analysis for
the exact signed build. After a candidate validates independently against the
required message, media, and contact groups, the Profile must still pass full
DB/WAL materialization and `PRAGMA quick_check` before it can be certified.
