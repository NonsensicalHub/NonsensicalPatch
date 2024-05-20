using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NonsensicalPatchUpdater;

public static class JsonOptionsCreater
{
    public static JsonSerializerOptions CreateDefaultOptions()
    {
        return new()
        {
            TypeInfoResolver = JsonSerializer.IsReflectionEnabledByDefault
                ? new DefaultJsonTypeInfoResolver()
                : SourceGenerationContext.Default
        };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UpdateConfig))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string))]
partial class SourceGenerationContext : JsonSerializerContext
{

}
