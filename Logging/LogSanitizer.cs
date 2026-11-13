using System.Text.Json.Nodes;

namespace PH.SapB1.Vaniperen.Logging;

public static class LogSanitizer
{
    private static readonly string[] SensitiveKeys =
    {
        "password",
        "pwd",
        "authorization",
        "cookie",
        "set-cookie",
        "token",
        "session",
        "b1session",
        "apikey",
        "api-key",
        "secret"
    };

    public static string MaskHeader(string headerName, string value)
    {
        if (IsSensitive(headerName))
            return "***MASKED***";

        return value;
    }

    public static string MaskBody(string? body, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        var trimmed = body.Trim();

        string result;

        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            result = TryMaskJson(trimmed);
        }
        else
        {
            result = MaskPlainText(trimmed);
        }

        if (result.Length > maxChars)
        {
            return result[..maxChars] + "...TRUNCATED";
        }

        return result;
    }

    private static string TryMaskJson(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);

            if (node is null)
                return json;

            MaskJsonNode(node);

            return node.ToJsonString();
        }
        catch
        {
            return MaskPlainText(json);
        }
    }

    private static void MaskJsonNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var keys = obj.Select(x => x.Key).ToList();

            foreach (var key in keys)
            {
                if (IsSensitive(key))
                {
                    obj[key] = "***MASKED***";
                    continue;
                }

                if (obj[key] is JsonNode child)
                {
                    MaskJsonNode(child);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr)
            {
                if (child is not null)
                {
                    MaskJsonNode(child);
                }
            }
        }
    }

    private static string MaskPlainText(string text)
    {
        // Basic protection for accidental password log text.
        foreach (var key in SensitiveKeys)
        {
            if (text.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return "***BODY_CONTAINS_SENSITIVE_DATA_MASKED***";
            }
        }

        return text;
    }

    private static bool IsSensitive(string key)
    {
        return SensitiveKeys.Any(x =>
            key.Contains(x, StringComparison.OrdinalIgnoreCase));
    }
}