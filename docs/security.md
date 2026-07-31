# Security boundary

All database probing uses SQLite read-only mode. Snapshots and exports can
contain private data and are ignored by Git. The helper protocol has no
operation for arbitrary memory reads, commands, keys, or decryption. Any future
privileged capability must be narrowly designed and independently reviewed.
