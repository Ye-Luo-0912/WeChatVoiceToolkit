param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

dotnet restore ..\WeChatVoice.slnx
dotnet build ..\WeChatVoice.slnx --configuration $Configuration --no-restore
dotnet test ..\WeChatVoice.slnx --configuration $Configuration --no-build
