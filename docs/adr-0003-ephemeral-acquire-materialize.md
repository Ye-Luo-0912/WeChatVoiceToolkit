# ADR 0003: Ephemeral acquire-and-materialize orchestration

## Status

Accepted. The transport, lifecycle, and Fake integration are implemented. No
real key-extraction Profile is registered yet.

## Decision

`workspace materialize` is the single formal product entry point. The CLI
verifies the raw Snapshot, creates a random one-time named pipe, launches the
installed `WeChatVoice.KeyBroker.exe` through UAC, sends only ProtocolVersion,
RequestId, SnapshotId, and `acquire-and-materialize`, and verifies the resulting
`local-workspace.json` before returning success.

The Broker is `requireAdministrator`, accepts one current-user pipe connection,
performs one operation, and exits. Its response contains only status, RequestId,
Profile ID, Materialization ID, and a bounded error. It never returns a key.

`EphemeralAcquireAndMaterializeService` owns sensitive lifetime. A successful
Profile passes database-group-bound `SensitiveBuffer` instances directly to an
ephemeral materializer. Disposal clears every binding on success, failure,
mismatch, or cancellation. Only the non-sensitive result persists.

Process identity and database behavior are Profile-driven rather than globally
hardcoded. Version, image hash, Authenticode trust/publisher, owner SID, session,
architecture, start time, and image path are verified before and after handle
acquisition. Memory reads are committed/readable-page only, bounded, chunked,
overlapped, and backed by deterministically cleared pooled buffers.

The current formal Profile registry is still empty. A non-registered
`WeixinWindows41155Profile` now exists as the next-stage candidate chain: it
binds exactly to version 4.1.11.55, image hash, and x64, loads every `.db`
first page from the verified Snapshot, scans only bounded readable process
regions, and requires one authenticated candidate for every database group.
It returns group-bound zeroing buffers to its caller but is not enabled by the
Broker until a real plaintext materializer and full output validation are
installed. The formal Broker therefore continues to return `profile_unavailable`
and never scans a live process in the current build.
