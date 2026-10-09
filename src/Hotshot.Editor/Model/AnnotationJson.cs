using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hotshot.Editor.Model;

/// <summary>On-disk shape of the annotations sidecar file.</summary>
internal sealed class AnnotationFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    public RectF? Crop { get; set; }

    public List<Annotation> Annotations { get; set; } = [];
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(Vector2JsonConverter), typeof(RectFJsonConverter), typeof(RgbaColorJsonConverter)])]
[JsonSerializable(typeof(AnnotationFile))]
[JsonSerializable(typeof(Annotation))]
internal sealed partial class EditorJsonContext : JsonSerializerContext;

internal sealed class Vector2JsonConverter : JsonConverter<Vector2>
{
    public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected [x, y].");
        }

        reader.Read();
        var x = reader.GetSingle();
        reader.Read();
        var y = reader.GetSingle();
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("Expected [x, y].");
        }

        return new Vector2(x, y);
    }

    public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(MathF.Round(value.X, 2));
        writer.WriteNumberValue(MathF.Round(value.Y, 2));
        writer.WriteEndArray();
    }
}

internal sealed class RectFJsonConverter : JsonConverter<RectF>
{
    public override RectF Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected [x, y, width, height].");
        }

        Span<float> v = stackalloc float[4];
        for (var i = 0; i < 4; i++)
        {
            reader.Read();
            v[i] = reader.GetSingle();
        }

        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("Expected [x, y, width, height].");
        }

        return new RectF(v[0], v[1], v[2], v[3]);
    }

    public override void Write(Utf8JsonWriter writer, RectF value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(MathF.Round(value.X, 2));
        writer.WriteNumberValue(MathF.Round(value.Y, 2));
        writer.WriteNumberValue(MathF.Round(value.Width, 2));
        writer.WriteNumberValue(MathF.Round(value.Height, 2));
        writer.WriteEndArray();
    }
}

internal sealed class RgbaColorJsonConverter : JsonConverter<RgbaColor>
{
    public override RgbaColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        RgbaColor.TryParse(reader.GetString(), out var c) ? c : throw new JsonException("Invalid color.");

    public override void Write(Utf8JsonWriter writer, RgbaColor value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToHex());
}
