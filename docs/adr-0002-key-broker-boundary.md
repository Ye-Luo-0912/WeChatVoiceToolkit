# ADR 0002: One-shot Key Broker boundary

## Status

Accepted for route two. The observed 4.1.11.55 extraction Profile is
`LiveValidated` for the exact executable and versioned WCDB module identities
recorded below. It is not a fallback for any other version.

## Decision

`WeChatVoice.ElevatedHelper` remains a metadata-only diagnostic service and
must never gain process-memory, key, or materialization operations.

Route two uses the separate `WeChatVoice.KeyBroker.exe` with a UAC
`requireAdministrator` manifest. The Broker is one-shot. Its request admits
only protocol version, Request ID, content-addressed Snapshot ID, and the fixed
`acquire-and-materialize` operation. The random 256-bit nonce is embedded in the
one-time pipe name rather than accepted as a request field.

PID, process name, address, read length, module base, database path, backend
executable, arbitrary arguments, and output commands are rejected. The Broker
verifies the complete raw Snapshot file set, lengths, hashes, and Snapshot ID
before Profile selection. It returns structured status and identifiers but no
key material.

Formal materialization uses the fixed, bundle-manifest-verified SQLCipher
Worker. The development external backend remains opt-in and untrusted. Profile
and database formats are extensible through reviewed registries, while the
permanent floor remains no arbitrary privileged command, no writable process
access, no raw-key output/persistence, and no unknown-version heuristic.

## Exact identity evidence

Read-only inspection of the supported installation on 2026-08-01 established:

- Weixin product/file version `4.1.11.55`;
- Weixin.exe SHA-256
  `ac599744a7ce7b65640ebe18c939c0d4e4a06cd039d89cddee7f1e9afc56875d`;
- versioned `4.1.11.55/Weixin.dll` SHA-256
  `ab925b9428239def44b252d970c337034d75e66b27eb5529633dc10669fc796a`;
- a trusted Authenticode signature whose subject contains Tencent;
- current user SID, Session ID, x64 architecture, image path, and start time,
  rechecked around read-only process opening.

Identity evidence alone does not imply key or schema semantics. The exact
Profile additionally recognizes the observed protected WCDB key-spec form and
validates candidates against the database first page using an exact SQLCipher
3/4 page-cipher set. Validation uses constant-time comparison and clears
derived buffers.

## Live validation result

A stable business Snapshot contained 21 encrypted database files with distinct
first-page salts. The Profile validated 20 required database groups. The fixed
Worker materialized all 20; each output passed plaintext SQLite header, Schema
Probe, hashing, and `PRAGMA quick_check`. The remaining migration-only
`migrate/unspportmsg.db` is explicitly recorded as `IntentionallyIgnored`.
Required message, media, and contact groups may not use that exception.

The generated verified workspace then completed account binding, contact
lookup, real incoming voice scan, raw SILK export, and repeat-run hash-safe
skips. No raw key was logged, returned, or persisted.
