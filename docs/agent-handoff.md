# Current handoff

`main` now has a real end-to-end path for the exact signed Weixin Windows
4.1.11.55 build:

`VerifiedRawSnapshot -> restricted key acquisition -> verified materialization
-> VerifiedLocalWorkspace -> contact -> voice scan -> raw SILK + run manifest`.

The Profile ID is
`weixin-windows-4.1.11.55-wcdb-protected-spec-v2`. It binds the exact
Weixin.exe version/hash and the versioned `4.1.11.55/Weixin.dll` hash, scans the
verified current-user process tree under fixed limits, recognizes the observed
protected WCDB key specifications, and accepts candidates only after exact
first-page HMAC validation. The database encryption Profile is retained per
database group and passed only through the private Broker-to-Worker stdin
envelope. Key material is never persisted or returned.

Live validation on 2026-08-01 used a stable 21-database Snapshot. Twenty
databases materialized successfully and passed SQLite header, Schema Probe,
hash, and `PRAGMA quick_check`. Only `migrate/unspportmsg.db` lacked a validated
key; its migration-only status is the sole exact policy exception and is
recorded as `IntentionallyIgnored`. Do not broaden this exception.

The built-in `weixin-windows-4` Adapter is versioned
`4.1.11.55-schema-v1`. Verified evidence is:

- contacts come from `contact.contact` and stable selection uses `username`;
- per-conversation message tables are `Msg_<lowercase MD5(username)>`;
- `local_type=34` is voice, `origin_source=2` is incoming, and
  `origin_source=5` is outgoing for this exact schema;
- media comes from `media_0.db/VoiceInfo`, with conversation mapped through
  `Name2Id.rowid`;
- association requires conversation plus the complete
  `(local_id, server_id/svr_id, create_time)` tuple. Never use local ID alone;
- payload BLOBs are streamed and hashed; missing and empty media are distinct
  scan outcomes.

A real incoming scan found linked SILK rows and a real export completed with no
failures. Repeating the same export verified and skipped all existing files,
confirming stable-key idempotency. Local snapshots, decrypted workspaces,
contacts, exports, and Manifests remain ignored and must never be committed.

Before release work, run the locked restore, CI Release build, format check,
and complete tests. The next useful work is a guided high-level CLI flow,
duration evidence, packaging, and only then derived WAV decoding. Do not add an
unknown-version Profile, partial message/media join, raw-key output, arbitrary
process reader, or caller-selected privileged executable.
