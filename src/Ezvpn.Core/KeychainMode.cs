namespace Ezvpn.Core;

/// <summary>
/// Where the app keeps its secrets, chosen once at launch from the
/// <c>EZVPN_KEYCHAIN</c> environment variable (<see cref="Variable"/>) — the
/// Windows counterpart of the Apple app's <c>KeychainMode</c>.
///
/// Unset, it is Windows Credential Manager. <c>mock</c> keeps every secret in
/// memory instead, for an end-to-end test or a QA run that must not touch this
/// machine's credentials or saved profiles: the secrets are gone when the app
/// quits, and the run keeps its profiles apart in a <c>mock-profiles</c>
/// directory emptied at every start.
/// </summary>
public static class KeychainMode
{
    public const string Variable = "EZVPN_KEYCHAIN";

    /// <summary>
    /// Whether <paramref name="value"/> asks for the in-memory mock. It takes
    /// <c>mock</c> or nothing: any other value is refused rather than read as
    /// Credential Manager, so a typo never lands a test run on the real
    /// secrets.
    /// </summary>
    public static bool IsMock(string? value) => value switch
    {
        null => false,
        "mock" => true,
        _ => throw new ArgumentException(
            $"{Variable} is \"{value}\"; the only value it takes is \"mock\"."),
    };
}
