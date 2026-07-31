# ADR 0005: Exact Weixin 4.1.11.55 Profile and voice Adapter

## Status

Accepted and live-validated on 2026-08-01.

## Decision

The first supported product target is the exact signed Weixin Windows
4.1.11.55 build. Support is the conjunction of executable version/hash,
Tencent signature, user/session/architecture identity, the fixed versioned
WCDB module hash, protected key-spec format, database page-cipher validation,
materialization acceptance, and exact schema mapping. Failure of any condition
is a hard no-match; there is no nearby-version or heuristic fallback.

The Profile may read only bounded committed, readable, non-guard process memory
from the verified current-user Weixin process tree. Candidate material remains
inside the elevated chain, is bound to a `DatabaseGroupFingerprint`, and is
cleared deterministically. The fixed Worker is selected by its complete bundle
manifest and accepts the exact database-encryption Profile ID chosen during
page validation.

The exact schema Adapter uses stable contact `username`, derives conversation
message table names as `Msg_<lowercase MD5(username)>`, treats `local_type=34`
as voice, and maps `origin_source` values 2/5 to incoming/outgoing. It associates
`VoiceInfo` only when conversation, local ID, server ID, and creation time all
match. This complete tuple is required because partial joins produced false
matches during analysis.

Original voice BLOBs are streamed from SQLite, hashed before export, and stored
under a path derived only from `SourceStableKey`. Snapshot and database hashes
are provenance rather than de-duplication identity.

## Consequences

- Other Weixin versions require new reviewed Profile and schema evidence.
- `migrate/unspportmsg.db` is the only exact intentionally-ignored input; this
  exception must not be generalized.
- Voice duration is currently unknown and remains a manifest quality flag.
- Raw SILK is the supported output. WAV and training preparation remain
  independent derived phases.
