using System.Text.Json;
using Shared.Enums;

namespace ClawTweaksCenter.Core
{
    internal static class HelperPipeProtocol
    {
        internal static bool TryParse(string json, out Function function, out string content)
        {
            function = Function.None;
            content = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("Function", out var ordinal) ||
                    ordinal.ValueKind != JsonValueKind.Number || !ordinal.TryGetInt32(out int value)) return false;

                string parsedContent = null;
                if (root.TryGetProperty("Content", out var payload))
                {
                    if (payload.ValueKind == JsonValueKind.String) parsedContent = payload.GetString();
                    else if (payload.ValueKind != JsonValueKind.Null) return false;
                }

                // Decode JSON exactly once. Sequential string replacements reinterpret escaped
                // backslashes in Windows paths as tabs/newlines and leave Unicode escapes intact.
                // Unknown ordinals remain valid for forward compatibility with newer helpers.
                function = (Function)value;
                content = parsedContent;
                return true;
            }
            catch (JsonException) { return false; }
        }
    }
}
