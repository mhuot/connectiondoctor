using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using TextBox = System.Windows.Controls.TextBox;

namespace ConnectionDoctor;

internal sealed class DashboardWindow : Window
{
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(20, 24, 29));
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(31, 37, 43));
    private static readonly Brush PrimaryTextBrush = new SolidColorBrush(Color.FromRgb(235, 239, 243));
    private static readonly Brush SecondaryTextBrush = new SolidColorBrush(Color.FromRgb(166, 176, 186));
    private static readonly Brush HealthyBrush = new SolidColorBrush(Color.FromRgb(67, 160, 71));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(251, 140, 0));
    private static readonly Brush CriticalBrush = new SolidColorBrush(Color.FromRgb(229, 57, 53));

    private readonly TextBlock collectorStatus = Text();
    private readonly TextBlock powerStatus = Text();
    private readonly TextBlock deviceStatus = Text();
    private readonly TextBlock baselineStatus = Text();
    private readonly TextBlock refreshedStatus = Text(SecondaryTextBrush, 12);
    private readonly TextBox topology = ReadOnlyText();
    private readonly StackPanel findings = new();
    private readonly StackPanel incidents = new();
    private readonly DispatcherTimer timer;
    private readonly DashboardDataLoader dataLoader = new();
    private int refreshInProgress;
    private bool closePermanently;

    public DashboardWindow()
    {
        Title = "ConnectionDoctor";
        Width = 1180;
        Height = 760;
        MinWidth = 850;
        MinHeight = 560;
        Background = BackgroundBrush;
        Foreground = PrimaryTextBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = BuildContent();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => RefreshNow();
        timer.Start();
        RefreshNow();
    }

    public async void RefreshNow()
    {
        if (Interlocked.Exchange(ref refreshInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var data = await Task.Run(dataLoader.Load).ConfigureAwait(false);
            if (!Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() => Render(data));
            }
        }
        catch (IOException exception)
        {
            await RenderLoadErrorAsync(exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            await RenderLoadErrorAsync(exception.Message);
        }
        catch (JsonException exception)
        {
            await RenderLoadErrorAsync(exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref refreshInProgress, 0);
        }
    }

    private async Task RenderLoadErrorAsync(string message)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            await Dispatcher.InvokeAsync(() => RenderLoadError(message));
        }
    }

    public void ClosePermanently()
    {
        closePermanently = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs eventArgs)
    {
        if (!closePermanently)
        {
            eventArgs.Cancel = true;
            Hide();
            return;
        }

        timer.Stop();
        base.OnClosing(eventArgs);
    }

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(22) };
        root.Children.Add(BuildHeader());

        var body = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        root.Children.Add(body);

        var left = new Grid { Margin = new Thickness(0, 0, 10, 0) };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var cards = new UniformGrid { Rows = 1, Columns = 4 };
        cards.Children.Add(Card("Collector", collectorStatus));
        cards.Children.Add(Card("Power", powerStatus));
        cards.Children.Add(Card("Connections", deviceStatus));
        cards.Children.Add(Card("Baseline", baselineStatus));
        left.Children.Add(cards);

        var topologyPanel = Panel("Current topology", topology);
        topologyPanel.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(topologyPanel, 1);
        left.Children.Add(topologyPanel);

        var right = new Grid { Margin = new Thickness(10, 0, 0, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var findingsScroll = new ScrollViewer { Content = findings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        right.Children.Add(Panel("Diagnosis", findingsScroll));
        var incidentScroll = new ScrollViewer { Content = incidents, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var incidentPanel = Panel("Recent incidents", incidentScroll);
        incidentPanel.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(incidentPanel, 1);
        right.Children.Add(incidentPanel);

        return root;
    }

    private UIElement BuildHeader()
    {
        var header = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);

        var refresh = new Button
        {
            Content = "Refresh",
            Padding = new Thickness(16, 7, 16, 7),
            Background = new SolidColorBrush(Color.FromRgb(30, 100, 180)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        refresh.Click += (_, _) => RefreshNow();
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);

        refreshedStatus.VerticalAlignment = VerticalAlignment.Center;
        refreshedStatus.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(refreshedStatus, Dock.Right);
        header.Children.Add(refreshedStatus);

        var title = new StackPanel();
        title.Children.Add(Text(PrimaryTextBrush, 26, "ConnectionDoctor"));
        title.Children.Add(Text(SecondaryTextBrush, 13, "USB-C, USB4, display, hub, power, and peripheral health"));
        header.Children.Add(title);
        return header;
    }

    private void Render(DashboardData data)
    {
        collectorStatus.Text = data.Collector.IsRunning ? "Healthy" : "Needs attention";
        collectorStatus.Foreground = data.Collector.IsRunning ? HealthyBrush : CriticalBrush;

        if (data.Snapshot is null)
        {
            powerStatus.Text = "Waiting";
            deviceStatus.Text = "Waiting";
        }
        else
        {
            var power = data.Snapshot.Power;
            var watts = power.BatteryRateMilliwatts is null
                ? "rate unavailable"
                : $"{Math.Abs(power.BatteryRateMilliwatts.Value) / 1000.0:F1} W";
            powerStatus.Text = $"{(power.LineOnline ? "AC" : "Battery")} · {power.BatteryPercent}%\n{watts}";
            powerStatus.Foreground = power.IsDeficit ? WarningBrush : PrimaryTextBrush;
            deviceStatus.Text = $"{DeviceFilters.ConnectionDevices(data.Snapshot).Count} present";
        }

        var comparison = data.BaselineComparison;
        if (comparison is null)
        {
            baselineStatus.Text = File.Exists(SnapshotStore.DefaultBaselinePath) ? "Unavailable" : "Not saved";
            baselineStatus.Foreground = WarningBrush;
        }
        else if (comparison.Findings.Count == 0 && comparison.Missing.Count == 0)
        {
            baselineStatus.Text = "Matches";
            baselineStatus.Foreground = HealthyBrush;
        }
        else
        {
            baselineStatus.Text = $"{comparison.Missing.Count} missing";
            baselineStatus.Foreground = comparison.Findings.Any(item => item.Severity == "critical")
                ? CriticalBrush
                : WarningBrush;
        }

        topology.Text = data.Topology;
        refreshedStatus.Text = $"Updated {data.LoadedAt:HH:mm:ss}";
        RenderFindings(data);
        RenderIncidents(data.Incidents);
    }

    private void RenderLoadError(string message)
    {
        collectorStatus.Text = "Refresh failed";
        collectorStatus.Foreground = CriticalBrush;
        refreshedStatus.Text = $"Error at {DateTimeOffset.Now:HH:mm:ss}";
        findings.Children.Clear();
        findings.Children.Add(Message(message, CriticalBrush));
    }

    private void RenderFindings(DashboardData data)
    {
        findings.Children.Clear();
        var all = new List<Finding>();
        if (data.Snapshot is not null)
        {
            all.AddRange(PowerDiagnosis.Analyze(data.Snapshot.Power));
        }
        if (data.BaselineComparison is not null)
        {
            all.AddRange(data.BaselineComparison.Findings.Where(item =>
                !all.Any(existing => existing.Title == item.Title)));
        }

        if (all.Count == 0)
        {
            findings.Children.Add(Message("No active findings.", HealthyBrush));
            return;
        }

        foreach (var finding in all)
        {
            var color = finding.Severity == "critical" ? CriticalBrush : WarningBrush;
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            block.Children.Add(Text(color, 15, finding.Title));
            block.Children.Add(Wrapped(finding.Explanation));
            block.Children.Add(Wrapped($"Action: {finding.Recommendation}", SecondaryTextBrush));
            findings.Children.Add(block);
        }
    }

    private void RenderIncidents(IReadOnlyList<ConnectionIncident> items)
    {
        incidents.Children.Clear();
        if (items.Count == 0)
        {
            incidents.Children.Add(Message("No recorded incidents.", HealthyBrush));
            return;
        }

        foreach (var incident in items.Take(8))
        {
            var disappeared = incident.Events.Count(entry => entry.Kind == RecorderEntryKinds.DeviceDisappeared);
            var appeared = incident.Events.Count(entry => entry.Kind == RecorderEntryKinds.DeviceAppeared);
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            block.Children.Add(Text(PrimaryTextBrush, 14, $"{incident.Start:MMM d HH:mm:ss} · {incident.Duration.TotalSeconds:F0}s"));
            block.Children.Add(Text(SecondaryTextBrush, 12, $"{disappeared} disappeared · {appeared} appeared"));
            foreach (var entry in incident.Events.Where(entry => entry.Device is not null).Take(4))
            {
                block.Children.Add(Wrapped($"• {entry.Device!.FriendlyName}", SecondaryTextBrush));
            }
            incidents.Children.Add(block);
        }
    }

    private static Border Card(string title, UIElement value)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(SecondaryTextBrush, 12, title.ToUpperInvariant()));
        if (value is FrameworkElement element)
        {
            element.Margin = new Thickness(0, 8, 0, 0);
        }
        stack.Children.Add(value);
        return new Border
        {
            Background = PanelBrush,
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(14),
            Child = stack
        };
    }

    private static Border Panel(string title, UIElement content)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = Text(PrimaryTextBrush, 16, title);
        heading.Margin = new Thickness(0, 0, 0, 12);
        grid.Children.Add(heading);
        Grid.SetRow(content, 1);
        grid.Children.Add(content);
        return new Border
        {
            Background = PanelBrush,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(16),
            Child = grid
        };
    }

    private static TextBlock Message(string message, Brush color) =>
        Text(color, 14, message);

    private static TextBlock Text(Brush? color = null, double size = 16, string? content = null) =>
        new()
        {
            Text = content ?? string.Empty,
            Foreground = color ?? PrimaryTextBrush,
            FontSize = size,
            FontFamily = new FontFamily("Segoe UI")
        };

    private static TextBlock Wrapped(string content, Brush? color = null) =>
        new()
        {
            Text = content,
            Foreground = color ?? PrimaryTextBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        };

    private static TextBox ReadOnlyText() =>
        new()
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 12,
            Background = Brushes.Transparent,
            Foreground = PrimaryTextBrush,
            BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap
        };
}
