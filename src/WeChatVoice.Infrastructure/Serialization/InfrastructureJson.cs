using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatVoice.Infrastructure.Serialization;

/// <summary>
/// JSON settings used for manifests and inspection reports written by this assembly.
/// </summary>
internal static class InfrastructureJson
{
    internal static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
