using Ezvpn.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Ezvpn.App.Views;

/// <summary>
/// On-demand "connection path" readout: a point-in-time snapshot of how the
/// running tunnel reaches the server (every live iroh relay/direct path, the one
/// in use marked "active") plus each custom relay's health — the Windows
/// counterpart of the Apple/Android sheet of the same name, and of
/// <c>ezvpn client status</c>.
///
/// The snapshot is taken when the dialog opens and again on Refresh, never on
/// the status poll timer: it makes a <c>/healthz</c> request per custom relay.
/// </summary>
public sealed class ConnPathDialog : ContentDialog
{
    private readonly Func<Task<ConnPathSnapshot?>> _query;
    private readonly StackPanel _body = new() { Spacing = 12, MinWidth = 520 };

    public ConnPathDialog(Func<Task<ConnPathSnapshot?>> query)
    {
        _query = query;
        Title = "Connection path";
        PrimaryButtonText = "Refresh";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        Content = new ScrollViewer { Content = _body };

        // Refresh re-queries in place instead of closing the dialog.
        PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                await RefreshAsync();
            }
            finally
            {
                deferral.Complete();
            }
        };
        Opened += async (_, _) => await RefreshAsync();
        Render(null, loading: true);
    }

    private async Task RefreshAsync()
    {
        IsPrimaryButtonEnabled = false;
        try
        {
            Render(await _query(), loading: false);
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
        }
    }

    private void Render(ConnPathSnapshot? snapshot, bool loading)
    {
        _body.Children.Clear();

        if (loading)
        {
            _body.Children.Add(new ProgressRing { IsActive = true, HorizontalAlignment = HorizontalAlignment.Left });
            return;
        }

        if (snapshot is null || snapshot.Paths.Count == 0)
        {
            _body.Children.Add(Caption("No path yet — still establishing. Refresh in a moment."));
        }
        else
        {
            foreach (var path in snapshot.Paths)
            {
                _body.Children.Add(PathRow(path));
            }
        }
        _body.Children.Add(Caption(
            "Snapshot taken just now — how this session reaches the server. Direct paths are " +
            "peer-to-peer; relay paths hop through an iroh relay."));

        if (snapshot is { CustomRelays.Count: > 0 })
        {
            _body.Children.Add(new TextBlock
            {
                Text = "Custom relays",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (var relay in snapshot.CustomRelays)
            {
                _body.Children.Add(RelayRow(relay));
            }
            _body.Children.Add(Caption(
                "Health is each relay's /healthz, checked just now. It confirms the relay is up, " +
                "not that the relay token is accepted."));
        }
    }

    private static UIElement PathRow(ConnPath path)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(path.Kind switch
            {
                "direct" => Colors.SeaGreen,
                "relay" => Colors.DarkOrange,
                _ => Colors.Gray,
            }),
        };
        row.Children.Add(dot);

        var text = new TextBlock
        {
            Text = path.Display,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (path.Selected)
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 1, 7, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Colors.SeaGreen) { Opacity = 0.18 },
                Child = new TextBlock
                {
                    Text = "active",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.SeaGreen),
                },
            };
            Grid.SetColumn(pill, 2);
            row.Children.Add(pill);
        }
        return row;
    }

    private static UIElement RelayRow(CustomRelayStatus relay)
    {
        var (state, brush) = relay.Working switch
        {
            true => ("Working", new SolidColorBrush(Colors.SeaGreen)),
            false => (relay.Error is { Length: > 0 } err ? $"Not working — {err}" : "Not working",
                (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]),
            null => ("Status unavailable", (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]),
        };
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = relay.Url,
            FontFamily = new FontFamily("Consolas"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = state,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };
}
