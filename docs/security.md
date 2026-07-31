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
not a generic decryptor and is not considered a production Weixin backend
until the exact version-bound Profile validates real database pages and WAL
behavior.
