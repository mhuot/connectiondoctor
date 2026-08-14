using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ConnectionDoctor;

/// <summary>
/// Notification-area presence: keeps the dashboard reachable from login and
/// shows at a glance whether the collector is recording.
///
/// This is a launcher and a status light, not a second UI. The views live in
/// the Connection Dashboard, served over HTTP by this same process, so there is
/// one implementation of the topology rather than a WPF one that drifts from
/// the React one.
/// </summary>
internal static class TrayApplication
{
    private const string MutexName = @"Local\ConnectionDoctor.Tray";

    // NotifyIcon tooltips are truncated by the shell; keep well inside it.
    private const int TooltipLimit = 63;

    private static readonly DashboardDataLoader Loader = new();

    public static int Run(int port)
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Console.Error.WriteLine("ConnectionDoctor tray is already running.");
            return 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Serve from the tray so the dashboard is up whenever the user is
        // logged in. If something already holds the port — `serve`, or a second
        // instance — this thread simply exits and the menu opens that one.
        var server = new Thread(() => ContractServer.Run(port, lan: false))
        {
            IsBackground = true,
            Name = "ConnectionDoctor contract server"
        };
        server.Start();

        using var menu = BuildMenu(port);
        using var icon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "ConnectionDoctor",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => ContractServer.OpenInBrowser(port);

        using var refresh = new System.Windows.Forms.Timer { Interval = 5000 };
        refresh.Tick += (_, _) => icon.Text = Summary();
        refresh.Start();
        icon.Text = Summary();

        Application.Run();
        icon.Visible = false;
        return 0;
    }

    private static ContextMenuStrip BuildMenu(int port)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => ContractServer.OpenInBrowser(port));
        menu.Items.Add("Copy status for a ticket", null, (_, _) => CopyStatus());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        return menu;
    }

    private static string Summary()
    {
        try
        {
            var data = Loader.Load();
            var state = data.Collector.IsRunning ? "recording" : "not recording";
            var text = $"ConnectionDoctor - {state}, {data.VisibleConnectionCount} devices";
            return text.Length > TooltipLimit ? text[..TooltipLimit] : text;
        }
        catch (IOException)
        {
            return "ConnectionDoctor";
        }
    }

    /// <summary>The paste-into-a-ticket summary the CLI would print.</summary>
    private static void CopyStatus()
    {
        try
        {
            var data = Loader.Load();
            var report = new StringBuilder();
            report.AppendLine(data.Collector.Message);
            report.AppendLine();
            report.AppendLine(data.Topology);

            if (data.BaselineComparison is { } comparison)
            {
                foreach (var finding in comparison.Findings)
                {
                    report.AppendLine($"{finding.Severity.ToUpperInvariant()}: {finding.Title}");
                    report.AppendLine($"  {finding.Explanation}");
                }
            }

            if (data.Incidents.Count > 0)
            {
                report.AppendLine($"Recorded incidents: {data.Incidents.Count}");
            }

            Clipboard.SetText(report.ToString());
        }
        catch (IOException)
        {
        }
        catch (ExternalException)
        {
            // Another process owns the clipboard; nothing useful to do here.
        }
    }
}
