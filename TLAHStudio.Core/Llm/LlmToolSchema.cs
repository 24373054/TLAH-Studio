using System.Text.Json;
using System.Text.Json.Nodes;

namespace TLAHStudio.Core.Llm;

public static class LlmToolSchema
{
    public static bool IsStrictNormalizable(Dictionary<string, object> schema)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(schema) as JsonObject;
            return node?["type"]?.GetValue<string>() == "object" &&
                   node["properties"] is JsonObject;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Converts the app's backward-compatible optional-property schemas to the
    /// strict function schema shape expected by OpenAI/Anthropic. The runtime
    /// schema remains unchanged; only the provider wire schema is normalized.
    /// </summary>
    public static Dictionary<string, object> NormalizeForStrictProvider(
        Dictionary<string, object> schema)
    {
        var root = JsonSerializer.SerializeToNode(schema) as JsonObject
                   ?? throw new InvalidOperationException("Tool schema must be a JSON object.");
        NormalizeObject(root);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(root.ToJsonString())
               ?? new Dictionary<string, object>();
    }

    private static void NormalizeNode(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return;

        var typeNames = ReadTypes(obj["type"]);
        if (typeNames.Contains("object", StringComparer.Ordinal))
            NormalizeObject(obj);
        if (typeNames.Contains("array", StringComparer.Ordinal))
            NormalizeNode(obj["items"]);

        foreach (var branchName in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (obj[branchName] is JsonArray branches)
                foreach (var branch in branches)
                    NormalizeNode(branch);
        }
    }

    private static void NormalizeObject(JsonObject obj)
    {
        obj["additionalProperties"] = false;
        if (obj["properties"] is not JsonObject properties)
            return;

        var originallyRequired = obj["required"] is JsonArray required
            ? required
                .Select(item => item?.GetValue<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var allRequired = new JsonArray();
        foreach (var property in properties.ToArray())
        {
            allRequired.Add(property.Key);
            if (property.Value is JsonObject propertySchema)
            {
                if (!originallyRequired.Contains(property.Key))
                    MakeNullable(propertySchema);
                NormalizeNode(propertySchema);
            }
        }
        obj["required"] = allRequired;
    }

    private static void MakeNullable(JsonObject schema)
    {
        var types = ReadTypes(schema["type"]);
        if (types.Count > 0 && !types.Contains("null", StringComparer.Ordinal))
        {
            var nullableTypes = new JsonArray();
            foreach (var type in types)
                nullableTypes.Add(type);
            nullableTypes.Add("null");
            schema["type"] = nullableTypes;
        }
        else if (types.Count == 0 && schema["anyOf"] is JsonArray anyOf)
        {
            if (!anyOf.Any(IsNullSchema))
                anyOf.Add(new JsonObject { ["type"] = "null" });
        }

        if (schema["enum"] is JsonArray enumValues &&
            !enumValues.Any(item => item == null))
            enumValues.Add(null);
    }

    private static bool IsNullSchema(JsonNode? node) =>
        node is JsonObject obj &&
        ReadTypes(obj["type"]).Contains("null", StringComparer.Ordinal);

    private static List<string> ReadTypes(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var type))
            return [type];
        if (node is JsonArray array)
            return array
                .Select(item => item is JsonValue child && child.TryGetValue<string>(out var itemType)
                    ? itemType
                    : null)
                .Where(item => item != null)
                .Cast<string>()
                .ToList();
        return [];
    }
}
