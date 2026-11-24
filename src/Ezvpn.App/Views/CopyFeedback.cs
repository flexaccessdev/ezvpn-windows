using Microsoft.UI.Xaml.Controls;

namespace Ezvpn.App.Views;

/// <summary>
/// Confirms a copy-to-clipboard: the copy button's icon turns into a check mark
/// for a moment, so a click that changed nothing visible still reads as done.
/// </summary>
internal static class CopyFeedback
{
    private const string CheckGlyph = "";
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(1.5);

    /// <summary>Flash a check mark on a button whose content is a <see cref="FontIcon"/>.</summary>
    public static async void Flash(Button? button)
    {
        if (button?.Content is not FontIcon icon || icon.Glyph == CheckGlyph)
        {
            return;
        }
        var original = icon.Glyph;
        icon.Glyph = CheckGlyph;
        // Resumes on the UI thread (WinUI installs a dispatcher sync context).
        await Task.Delay(Duration);
        icon.Glyph = original;
    }
}
