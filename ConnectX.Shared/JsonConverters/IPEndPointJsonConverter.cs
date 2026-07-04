using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectX.Shared.JsonConverters;

public class IPEndPointJsonConverter : JsonConverter<IPEndPoint>
{
    public override IPEndPoint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var endpointString = reader.GetString();
        if (string.IsNullOrWhiteSpace(endpointString))
            return null;

        // Try parse IP:Port format
        if (IPEndPoint.TryParse(endpointString, out var endpoint))
            return endpoint;
        throw new JsonException($"Invalid IPEndPoint format: {endpointString}");
    }

    public override void Write(Utf8JsonWriter writer, IPEndPoint value, JsonSerializerOptions options)
    {
        var endpointString = $"{value.Address}:{value.Port}";
        writer.WriteStringValue(endpointString);
    }
}