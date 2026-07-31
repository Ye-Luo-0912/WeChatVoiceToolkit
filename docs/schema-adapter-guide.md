# Schema adapter guide

An adapter must implement `IWeChatVoiceSchemaAdapter` and operate only on a
verified schema snapshot. It should map source records into the stable
`VoiceMessage` model and stream payloads through `IVoiceSource`. Do not encode
unverified table, column, or encryption assumptions in shared code.
