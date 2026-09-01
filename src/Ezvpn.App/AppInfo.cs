using System.Reflection;

namespace Ezvpn.App;

/// <summary>
/// App-level metadata surfaced in the UI. The app version comes from the
/// assembly's informational version (set by &lt;Version&gt; in Ezvpn.App.csproj from
/// EzvpnAppVersion, overridable from CI); any build-metadata suffix (e.g.
/// "+&lt;commit&gt;" added by SourceLink) is trimmed off. The core version is the
/// pinned ezvpn.dll release (native.targets EzvpnReleaseTag), stamped into the
/// assembly as the "EzvpnCoreVersion" metadata entry. The two move independently.
/// </summary>
public static class AppInfo
{
    /// <summary>The app version formatted for display, e.g. "v0.1.0".</summary>
    public static string Version { get; } = ComputeVersion();

    /// <summary>
    /// The pinned ezvpn core release formatted for display, e.g. "v0.0.44". A
    /// local core build (EZVPN_LOCAL_DLL) still reports the pinned number — the
    /// local DLL carries none.
    /// </summary>
    public static string CoreVersion { get; } = ComputeCoreVersion();

    /// <summary>Both numbers on one line, e.g. "v0.1.0 · core v0.0.44".</summary>
    public static string Summary { get; } = $"{Version} · core {CoreVersion}";

    private static string ComputeVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var version = informational is { Length: > 0 }
            ? informational.Split('+', 2)[0]
            : Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        return "v" + version;
    }

    private static string ComputeCoreVersion()
    {
        var core = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "EzvpnCoreVersion")?.Value;

        return core is { Length: > 0 } ? "v" + core : "unknown";
    }
}
