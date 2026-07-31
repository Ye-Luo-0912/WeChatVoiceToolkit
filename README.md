# WeChatVoiceToolkit

A .NET 10, Windows-focused foundation for safely inspecting user-provided
WeChat data snapshots and exporting voice media through version-specific schema
adapters.

The repository intentionally does **not** contain a WeChat key extractor,
database decryption logic, guessed schema mappings, or a UI. Those capabilities
require verified, user-provided version and schema information.

## Current commands

```powershell
dotnet run --project src/WeChatVoice.Cli -- doctor
dotnet run --project src/WeChatVoice.Cli -- snapshot create --source <directory> --output <workspace>
dotnet run --project src/WeChatVoice.Cli -- schema probe --database <database> --output <schema.json>
echo '{"requestId":"1","operation":"ping"}' | dotnet run --project src/WeChatVoice.ElevatedHelper
```

See [architecture.md](docs/architecture.md), [security.md](docs/security.md),
and [agent-handoff.md](docs/agent-handoff.md) before extending the project.
