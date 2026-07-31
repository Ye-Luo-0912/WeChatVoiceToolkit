# ADR 0003: Ephemeral acquire-and-materialize orchestration

## Status

Accepted. The transport, lifecycle, guarded 4.1.11.55 acquisition chain, and
synthetic SQLCipher materialization integration are implemented. The chain is
not yet certified against the user's live Weixin business databases.

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

The materializer now declares both `BackendId` and
`EncryptionProfileId`. Acquisition construction rejects duplicate groups and
any binding whose SnapshotId or ProfileId differs from the acquisition. The
orchestrator also rejects a materializer result from the wrong Snapshot or
Backend, so a future SQLCipher implementation cannot accidentally consume a
key for a different database format.

Process identity and database behavior are Profile-driven rather than globally
hardcoded. Version, image hash, Authenticode trust/publisher, owner SID, session,
architecture, start time, and image path are verified before and after handle
acquisition. Memory reads are committed/readable-page only, bounded, chunked,
overlapped, and backed by deterministically cleared pooled buffers.

The Broker now composes the guarded `WeixinWindows41155Profile` with a
database-group-bound `ProfileDrivenKeyAcquisitionService` and the one-shot
`SqlCipherEphemeralDatabaseMaterializer`. The latter invokes a fixed
`WeChatVoice.SqlCipherWorker` child with a bounded binary stdin envelope,
copies DB/WAL/SHM into a private staging area, exports plaintext SQLite, runs
header/schema/`quick_check` validation, writes a materialization manifest, and
creates the executable Local Workspace. The Worker is a compatibility runtime
validated by synthetic fixtures; it is not evidence that the observed Weixin
business format is SQLCipher until the exact Profile passes on real data.
