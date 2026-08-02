# Security boundary

The product owns the full `snapshot -> acquire -> materialize -> workspace`
flow. The CLI generates a 256-bit one-time pipe token, launches the installed
Key Broker with `runas`, sends one fixed operation, validates the response, and
then verifies the generated local workspace. Users and Agents do not assemble
memory commands or plaintext key files.

Safe seams remain extensible: signed Weixin identities, exact-version
key-extraction Profiles, database-encryption Profiles, output mappings, and
materializers are registries. The permanent safety floor is:

- no caller-selected PID, process name, address, module base, or read length;
- no `PROCESS_ALL_ACCESS`, VM write, injection, remote thread, or DLL loading;
- no arbitrary privileged command or caller-selected executable in formal mode;
- no raw key in the Broker named-pipe protocol, stdout, logs, exceptions,
  manifests, or persistent files; the only exception is the bounded private
  stdin envelope from Broker to the fixed SQLCipher Worker;
- no unknown-version heuristic Profile fallback;
- every key is database-group bound, validated, and deterministically cleared;
- all database probing remains read-only and local artifacts remain ignored by Git.

The pipe uses an explicit ACL for the current user SID, local administrators,
and the operating system. It intentionally does not request a mandatory-label
SACL because that would require `SeSecurityPrivilege` just to create the pipe.
The random token is part of the pipe name and never appears in the JSON request,
and the CLI verifies the connected server PID. The Broker accepts one
connection and one request, then exits.

The current materialization path uses the fixed SQLCipher Worker as an
isolated compatibility runtime. The Worker receives the validated key only
through its private stdin envelope, accepts a fixed input/output pair, copies
the DB/WAL/SHM group to private staging, and removes failed staging/output
artifacts. It never emits a key or accepts arbitrary commands. This runtime is
not a generic decryptor. The exact signed Weixin 4.1.11.55 Profile has been
live-validated against real database groups through materialization and
`PRAGMA quick_check`; any other executable version, image hash, WCDB module
hash, key-spec protection, or page-cipher profile is rejected.

The fixed module path is `<verified-install-root>/4.1.11.55/Weixin.dll`; the
Broker does not search `PATH` or arbitrary directories. One exact migration
auxiliary database may be recorded as intentionally ignored, but required
message, media, and contact databases must all materialize successfully.

## Broker binary trust

Before the CLI elevates the Key Broker, the adjacent
`WeChatVoice.KeyBroker.exe` must pass one of two exclusive trust policies.
There is no silent fallback: a build that fails the Release policy is refused
unless the user explicitly opts into the development policy.

- **ReleaseBrokerTrustPolicy (default, fail-closed)** requires, in order:
  1. a regular, non-reparse-point file in the fixed CLI install directory;
  2. a `WeChatVoice.KeyBroker.bundle.json` publish manifest whose `publisherThumbprint`
     is pinned non-empty and whose EXE hash binds the actual binary;
  3. full Authenticode verification of the EXE via `WinVerifyTrust`
     (`AuthenticodeVerifier` is the single authoritative implementation);
  4. the signer certificate SHA-256 digest equal to the pinned publisher
     thumbprint;
  5. an install directory the invoking user cannot write to (Program Files /
     MSIX-style containers satisfy this).
  Any missing or mismatched check denies elevation.
- **DevelopmentBrokerTrustPolicy** is never the default. It requires the
  explicit `--allow-development-broker` CLI flag **and** that the Broker is a
  regular file inside a verified repository build output (`src/*/bin` or
  `artifacts/`). It accepts an unsigned binary and prints an explicit warning
  that the build is development-only.
- `scripts/package-release.ps1` is the single complete-layout entry point. It
  reuses `scripts/publish-smoke.ps1` to generate and verify both the worker and
  the Broker bundle manifests; `scripts/sign-release.ps1` signs and verifies
  all four published executables when a certificate is supplied, and the
  publish smoke fails in CI when the output is unsigned.

## Private staging hardening

Broker-created private copies (the staged snapshot and the materialization
staging directory) are restricted to SYSTEM and local Administrators only,
without inherited rules, whenever the Broker runs elevated. Ordinary
same-user processes cannot replace or modify the temporary copies. Setting the
DACL fails closed; non-elevated development/test runs skip the restriction so
the surrounding flow remains testable.

## Export integrity

An export run performs one streaming read of each source BLOB and decides the
artifact identity at commit time. Existing SILK bytes are always hashed again
before they are treated as a verified skip; `artifact-index.jsonl` is an
incremental bookkeeping index, not cryptographic evidence. Otherwise the
source is read once and compared against the existing artifact.
`latest.manifest.json` and each run manifest inherit the full
materialization provenance (key-extraction Profile, Weixin version, module
hashes, backend bundle), so a training or dataset consumer can audit exactly
which verified source produced the voices.
