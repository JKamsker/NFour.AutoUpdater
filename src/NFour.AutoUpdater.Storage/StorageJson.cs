namespace NFour.AutoUpdater.Storage;

public sealed class StorageCapabilitiesJsonConverter : JsonConverter<StorageCapabilities>
{
    public override StorageCapabilities Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return (StorageCapabilities)reader.GetInt32();
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Storage capabilities must be an array.");
        var result = StorageCapabilities.None;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var value = reader.GetString()?.ToLowerInvariant() switch
            {
                "read" => StorageCapabilities.Read, "range" => StorageCapabilities.Range, "list" => StorageCapabilities.List, "write" => StorageCapabilities.Write,
                "conditionalwrite" or "conditional-write" => StorageCapabilities.ConditionalWrite, "serversidecopy" or "server-side-copy" => StorageCapabilities.ServerSideCopy,
                "presigning" => StorageCapabilities.Presigning, "multipart" => StorageCapabilities.Multipart, "delete" => StorageCapabilities.Delete, _ => StorageCapabilities.None
            };
            result |= value;
        }
        return result;
    }
    public override void Write(Utf8JsonWriter writer, StorageCapabilities value, JsonSerializerOptions options)
    {
        writer.WriteStartArray(); foreach (var (flag, name) in new[] { (StorageCapabilities.Read, "read"), (StorageCapabilities.Range, "range"), (StorageCapabilities.List, "list"), (StorageCapabilities.Write, "write"), (StorageCapabilities.ConditionalWrite, "conditional-write"), (StorageCapabilities.ServerSideCopy, "server-side-copy"), (StorageCapabilities.Presigning, "presigning"), (StorageCapabilities.Multipart, "multipart"), (StorageCapabilities.Delete, "delete") }) if (value.HasFlag(flag)) writer.WriteStringValue(name); writer.WriteEndArray();
    }
}
