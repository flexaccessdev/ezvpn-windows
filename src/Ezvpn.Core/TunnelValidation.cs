using System.Net;
using System.Net.Sockets;

namespace Ezvpn.Core;

/// <summary>
/// Pure, testable validation of profile fields — the .NET counterpart of the
/// Apple app's <c>TunnelNameValidation</c> / <c>IPPrefix</c>. Deeper checks
/// (whether the node id is a real iroh endpoint id, whether a routed prefix
/// overlaps the local network) are enforced by the Rust engine and surfaced as
/// start/status errors; this is fast client-side feedback only.
/// </summary>
public static class TunnelValidation
{
    public const int MaxNameLength = 64;

    /// <summary>Validate a profile name. Returns null if valid, else an error message.</summary>
    public static string? ValidateName(string? name, IEnumerable<string>? existingNames = null)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "Name is required.";
        }
        if (trimmed.Length > MaxNameLength)
        {
            return $"Name must be at most {MaxNameLength} characters.";
        }
        if (existingNames is not null &&
            existingNames.Any(n => string.Equals(n.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return "A profile with this name already exists.";
        }
        return null;
    }

    /// <summary>
    /// Light client-side check of the iroh server node id. The Rust core does the
    /// authoritative parse; here we only reject empty/whitespace input so the
    /// form gives immediate feedback.
    /// </summary>
    public static string? ValidateServerNodeId(string? nodeId)
    {
        var trimmed = (nodeId ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "Server node id is required.";
        }
        if (trimmed.Any(char.IsWhiteSpace))
        {
            return "Server node id must not contain spaces.";
        }
        return null;
    }

    /// <summary>
    /// Validate the auth token. It is required — a null/blank token returns an
    /// error message; otherwise null.
    /// </summary>
    public static string? ValidateAuthToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "Auth token is required.";
        }
        return null;
    }

    /// <summary>Validate a single CIDR string for the given family. Null = valid.</summary>
    public static string? ValidateCidr(string? cidr, bool ipv6)
    {
        var trimmed = (cidr ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "Empty route.";
        }
        if (!IPNetwork.TryParse(trimmed, out var net))
        {
            return $"'{trimmed}' is not a valid CIDR.";
        }
        var family = net.BaseAddress.AddressFamily;
        if (ipv6 && family != AddressFamily.InterNetworkV6)
        {
            return $"'{trimmed}' is not an IPv6 route.";
        }
        if (!ipv6 && family != AddressFamily.InterNetwork)
        {
            return $"'{trimmed}' is not an IPv4 route.";
        }
        return null;
    }

    /// <summary>Validate every route in a list; returns the first error, or null.</summary>
    public static string? ValidateRoutes(IEnumerable<string> routes, bool ipv6)
    {
        foreach (var r in routes)
        {
            var err = ValidateCidr(r, ipv6);
            if (err is not null)
            {
                return err;
            }
        }
        return null;
    }

    /// <summary>
    /// Parse a comma/newline/space-separated route list into trimmed, non-empty
    /// entries — for turning an edit-form text box into a list.
    /// </summary>
    public static List<string> SplitList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }
        return text
            .Split(new[] { ',', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }
}
