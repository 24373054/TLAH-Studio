using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TLAHStudio.Core.Helpers;

public static partial class SecretRedactor
{
    private static readonly string[] SensitiveNameParts =
    [
        "api_key",
        "apikey",
        "access_token",
        "refresh_token",
        "authorization",
        "bearer",
        "secret",
        "password",
        "x-api-key"
    ];

    public const string Redacted = "[REDACTED]";

    public static string RedactJson(string json, params string?[] knownSecrets)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        try
        {
            var node = JsonNode.Parse(json);
            if (node == null)
                return RedactText(json, knownSecrets);

            RedactNode(node, knownSecrets.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray());
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return RedactText(json, knownSecrets);
        }
    }

    public static string RedactObject(object value, params string?[] knownSecrets) =>
        RedactJson(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), knownSecrets);

    public static bool ContainsSecret(string text, params string?[] knownSecrets)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        var redacted = RedactText(text, knownSecrets);
        return !string.Equals(text, redacted, StringComparison.Ordinal);
    }

    private static void RedactNode(JsonNode node, IReadOnlyCollection<string> knownSecrets)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Value == null)
                    continue;

                if (IsSensitiveName(property.Key))
                {
                    obj[property.Key] = Redacted;
                    continue;
                }

                if (property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    ContainsSecret(text, knownSecrets.ToArray()))
                {
                    obj[property.Key] = RedactText(text, knownSecrets.ToArray());
                    continue;
                }

                RedactNode(property.Value, knownSecrets);
            }
            return;
        }

        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] == null)
                    continue;

                if (array[i] is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    ContainsSecret(text, knownSecrets.ToArray()))
                {
                    array[i] = RedactText(text, knownSecrets.ToArray());
                    continue;
                }

                RedactNode(array[i]!, knownSecrets);
            }
        }
    }

    public static string RedactText(string text, params string?[] knownSecrets)
    {
        var result = text;
        foreach (var secret in knownSecrets.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!))
            result = result.Replace(secret, Redacted, StringComparison.Ordinal);

        result = BearerRegex().Replace(result, "Bearer " + Redacted);
        result = ApiKeyRegex().Replace(result, Redacted);
        return result;
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return SensitiveNameParts.Any(part => normalized.Contains(part, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-+/=]{12,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b(sk|ak|pk)-[A-Za-z0-9_\-]{12,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyRegex();
}
