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
- no raw key in IPC, stdout, logs, exceptions, manifests, or persistent files;
- no unknown-version heuristic Profile fallback;
- every key is database-group bound, validated, and deterministically cleared;
- all database probing remains read-only and local artifacts remain ignored by Git.

The pipe uses `CurrentUserOnly`; an elevated process retains the same user SID,
which is stricter than allowing every local administrator. Its random token is
part of the pipe name and never appears in the JSON request. The Broker accepts
one connection and one request, then exits.
