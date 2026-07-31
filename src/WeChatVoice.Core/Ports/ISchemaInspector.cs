using WeChatVoice.Core.Models;

namespace WeChatVoice.Core.Ports;

/// <summary>
/// Reads database structure without interpreting application data.
/// </summary>
public interface ISchemaInspector
{
    Task<SchemaSnapshot> InspectAsync(string databasePath, CancellationToken cancellationToken);
}
