# Architecture

`WeChatVoice.Core` contains stable models and ports. `Application` orchestrates
voice exports. `Infrastructure` implements snapshotting, readonly SQLite schema
inspection, file export, and external SILK decoding. `Windows` contains local
process primitives. `Cli` composes the allowed operations, while
`ElevatedHelper` has a deliberately tiny JSON Lines protocol.

Schema adapters are the only version-specific layer. They are introduced only
after inspecting a verified, user-provided schema.
