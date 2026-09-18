using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Services;

public static class AuthIdentity
{
    private static readonly HashSet<string> AccountIdNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "account_id", "accountId", "chatgpt_account_id", "chatgptAccountId"
    };

    public static string ValidateAndFingerprint(ReadOnlySpan<byte> authJson)
    {
        if (authJson.IsEmpty)
        {
            throw new InvalidDataException("The authentication file is empty.");
        }

        using var document = JsonDocument.Parse(authJson.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The authentication file format is invalid.");
        }

        var root = document.RootElement;
        if ((root.TryGetProperty("auth_mode", out var mode) &&
             (mode.ValueKind != JsonValueKind.String || mode.GetString() != "chatgpt")) ||
            !root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object ||
            !tokens.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(access.GetString()))
            throw new InvalidDataException("A saved ChatGPT sign-in is required. API-key accounts are not supported.");

        var accountId = FindAccountId(tokens) ?? FindAccountIdInJwt(root);
        if (accountId is null)
            throw new InvalidDataException("The ChatGPT account identity is missing. Sign in again in Codex.");
        var identityBytes = Encoding.UTF8.GetBytes($"codex-account:{accountId}");

        return Convert.ToHexString(SHA256.HashData(identityBytes));
    }

    private static string? FindAccountId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (AccountIdNames.Contains(property.Name)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return property.Value.GetString()!.Trim();
                }

                var nested = FindAccountId(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindAccountId(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? FindAccountIdInJwt(JsonElement root)
    {
        if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var tokenName in new[] { "access_token", "id_token" })
        {
            if (!tokens.TryGetProperty(tokenName, out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2)
                {
                    continue;
                }

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
                using var jwt = JsonDocument.Parse(Convert.FromBase64String(payload));
                var found = FindAccountId(jwt.RootElement);
                if (found is not null)
                {
                    return found;
                }
            }
            catch
            {
                // An opaque token requires an explicit account ID in the tokens object.
            }
        }

        return null;
    }
}
