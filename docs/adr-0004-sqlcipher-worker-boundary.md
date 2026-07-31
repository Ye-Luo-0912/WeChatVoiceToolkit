# ADR 0004: Fixed SQLCipher worker boundary

## Status

Accepted for compatibility validation and the guarded Weixin 4.1.11.55
development path. It is not a heuristic decryption fallback.

## Decision

The privileged Key Broker owns process verification and ephemeral key
acquisition. It does not load a second SQLite runtime into the broker process.
Instead, it starts a fixed `WeChatVoice.SqlCipherWorker` child for each
verified database group and writes a bounded `WCV1` envelope to the child's
stdin. The envelope contains only the key length and key bytes; the Broker
never returns or persists the key.

The Worker accepts exactly `--input <path> --output <path>`, refuses existing
destinations and trailing envelope bytes, copies the verified DB/WAL/SHM group
to private staging, opens it with the e_sqlcipher provider, validates the key
with `quick_check`, exports to a plaintext SQLite destination, and validates
the resulting SQLite header and output before returning. Output and errors are
generic and bounded. Managed key buffers and envelope bytes are cleared in
`finally`; the textual SQLCipher raw-key representation is zeroed before the
command is released.

The Worker is intentionally a separate runtime because the main query process
continues to use the Windows SQLite provider. The package is a compatibility
runtime only: the exact Weixin database page format, KDF, HMAC, and WAL
semantics must be proven by the version-bound Profile before this path can be
called a production Weixin decryptor.

## Consequences

- Synthetic encrypted fixtures exercise the complete child-process path in CI.
- Wrong keys, malformed requests, trailing bytes, and existing destinations
  fail closed without changing the destination.
- A future SQLCipher upgrade must update the Worker version, lock file,
  compatibility tests, and backend hash contract together.
- The Worker does not provide a general-purpose process-memory or decryption
  service and cannot be invoked with caller-supplied commands or PIDs.
