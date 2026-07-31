# ADR 0001: Keep the query runtime on ordinary SQLite

Status: accepted

The toolkit chooses the decrypted-output route: a future decryptor must write
ordinary SQLite files into a controlled temporary directory, and schema/query
processes use the Windows `winsqlite3` provider only. No generic Infrastructure
class may implicitly switch to SQLCipher or freeze a different provider.

This keeps the current `Microsoft.Data.Sqlite.Core` + `winsqlite3` process
deterministic. If page decryption is added later, it belongs in an isolated
decryptor process with a fixed protocol and no key or arbitrary-memory escape
hatch.
