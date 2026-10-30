using System.Windows;
using System.Windows.Controls;
using AIMeter.Core;

namespace AIMeter.App;

/// <summary>Settings apply as you change them; there's no Save button.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppController _app;
    private bool _loading = true;

    public SettingsWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        var s = app.Settings;

        SizeSlider.Value = s.GaugeSize;
        OpacitySlider.Value = Math.Round(s.Opacity * 100);
        DarkEdgeBox.IsChecked = s.DarkEdge;
        ColorBox.IsChecked = s.ColorByUsage;
        AmberBox.Text = s.AmberAt.ToString();
        RedBox.Text = s.RedAt.ToString();
        StartupBox.IsChecked = s.StartWithWindows;
        RefreshBox.SelectedItem = RefreshBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == s.RefreshMinutes.ToString()) ?? RefreshBox.Items[2];
        UpdateLabels();

        SizeSlider.ValueChanged += (_, _) => Changed();
        OpacitySlider.ValueChanged += (_, _) => Changed();
        DarkEdgeBox.Click += (_, _) => Changed();
        ColorBox.Click += (_, _) => Changed();
        AmberBox.LostFocus += (_, _) => Changed();
        RedBox.LostFocus += (_, _) => Changed();
        StartupBox.Click += (_, _) => Changed();
        RefreshBox.SelectionChanged += (_, _) => Changed();

        _app.Changed += BuildProviderRows;
        Closed += (_, _) =>
        {
            _app.Changed -= BuildProviderRows;
            _app.MoveMode = false;
        };
        BuildProviderRows();
        _loading = false;
    }

    private void Changed()
    {
        if (_loading) return;
        var s = _app.Settings;
        s.GaugeSize = SizeSlider.Value;
        s.Opacity = OpacitySlider.Value / 100;
        s.DarkEdge = DarkEdgeBox.IsChecked == true;
        s.ColorByUsage = ColorBox.IsChecked == true;
        if (int.TryParse(AmberBox.Text, out var amber)) s.AmberAt = Math.Clamp(amber, 1, 100);
        if (int.TryParse(RedBox.Text, out var red)) s.RedAt = Math.Clamp(red, 1, 100);
        AmberBox.Text = s.AmberAt.ToString();
        RedBox.Text = s.RedAt.ToString();
        s.StartWithWindows = StartupBox.IsChecked == true;
        if (RefreshBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var minutes))
            s.RefreshMinutes = minutes;
        UpdateLabels();
        _app.SettingsChanged();
    }

    private void UpdateLabels()
    {
        SizeText.Text = $"{SizeSlider.Value:0} px";
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        MoveButton.Content = _app.MoveMode ? "Done moving" : "Move overlay";
    }

    private void BuildProviderRows()
    {
        ProviderRows.Children.Clear();
        var now = DateTimeOffset.Now;
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            var connected = _app.Settings.Connected.Contains(id);
            var state = _app.StateFor(id);
            var status = !connected ? "Not connected"
                : state.SignedOut ? "Sign-in expired"
                : state.Last is { } snap ? (snap.Plan is null ? "" : snap.Plan + " · ") + UsageText.Describe(snap, now)
                : state.LastError ?? "Waiting for data";

            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock { Text = ProviderInfo.Name(id), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock
            {
                Text = status, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(text, 1);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(buttons, 2);
            if (!connected || state.SignedOut)
            {
                var connect = new Button { Content = connected ? "Reconnect" : "Connect" };
                connect.Click += (_, _) => _app.Connect(id);
                buttons.Children.Add(connect);
            }
            if (connected)
            {
                var disconnect = new Button { Content = "Disconnect", Margin = new Thickness(6, 0, 0, 0) };
                disconnect.Click += async (_, _) => await _app.DisconnectAsync(id);
                buttons.Children.Add(disconnect);
            }

            grid.Children.Add(name);
            grid.Children.Add(text);
            grid.Children.Add(buttons);
            ProviderRows.Children.Add(grid);
        }
        UpdateLabels();
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        _app.MoveMode = !_app.MoveMode;
        UpdateLabels();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _app.RefreshAllAsync();
}
