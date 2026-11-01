using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentSignals.Api.Protocol;

/// <summary>A privacy concern found in a signal. Holds the JSON path, never the matched value.</summary>
public sealed record PrivacyFinding(string Path, string Kind, bool Blocking);

/// <summary>
/// Enforces the SIGNAL.md privacy rules at the door. Secrets and email addresses block the
/// signal (it is not stored). URLs are reported as protocol issues because SIGNAL.md asks for
/// anonymized identifiers instead of repository URLs, but they do not block storage.
/// This is a safety net, not a guarantee: agents remain responsible for what they emit.
/// </summary>
public static partial class PrivacyScanner
{
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})\b")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"(AccountKey|SharedAccessKey|SharedAccessSignature|ClientSecret|Password|pwd)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringSecret();

    [GeneratedRegex(@"[?&]sig=[A-Za-z0-9%/+=]{16,}")]
    private static partial Regex SasSignature();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"https?://", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    private static readonly (Func<Regex> Pattern, string Kind)[] Blocking =
    [
        (PrivateKey, "private key"),
        (Jwt, "access token"),
        (GitHubToken, "GitHub token"),
        (AwsAccessKey, "cloud access key"),
        (ConnectionStringSecret, "connection-string secret"),
        (SasSignature, "shared access signature"),
        (Email, "email address"),
    ];

    public static IReadOnlyList<PrivacyFinding> Scan(JsonElement signal)
    {
        var findings = new List<PrivacyFinding>();
        Walk(signal, "$", findings);
        return findings;
    }

    private static void Walk(JsonElement element, string path, List<PrivacyFinding> findings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, $"{path}.{SignalValidator.SafeName(property.Name)}", findings);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{index++}]", findings);
                }

                break;
            case JsonValueKind.String:
                Inspect(element.GetString()!, path, findings);
                break;
        }
    }

    private static void Inspect(string value, string path, List<PrivacyFinding> findings)
    {
        foreach (var (pattern, kind) in Blocking)
        {
            if (pattern().IsMatch(value))
            {
                findings.Add(new PrivacyFinding(path, kind, Blocking: true));
                return;
            }
        }

        if (Url().IsMatch(value))
        {
            findings.Add(new PrivacyFinding(path, "URL", Blocking: false));
        }
    }
}
