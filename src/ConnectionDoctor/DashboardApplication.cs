using System.Drawing;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace ConnectionDoctor;

internal static class DashboardApplication
{
    private const string MutexName = @"Local\ConnectionDoctor.Dashboard";
    private const string ShowEventName = @"Local\ConnectionDoctor.Dashboard.Show";
    private static System.Windows.Forms.NotifyIcon? trayIcon;
    private static DashboardWindow? window;

    public static void Run(bool showImmediately)
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!createdNew)
        {
            if (showImmediately)
            {
                showEvent.Set();
            }
            return;
        }

        var application = new WpfApplication
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        window = new DashboardWindow();
        window.Closing += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            window.Hide();
        };

        trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "ConnectionDoctor",
            Visible = true,
            ContextMenuStrip = BuildContextMenu(application)
        };
        trayIcon.DoubleClick += (_, _) => ShowWindow();
        var showThread = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                if (application.Dispatcher.HasShutdownStarted)
                {
                    return;
                }
                application.Dispatcher.BeginInvoke(ShowWindow);
            }
        })
        {
            IsBackground = true,
            Name = "ConnectionDoctor dashboard activation"
        };
        showThread.Start();

        if (showImmediately)
        {
            ShowWindow();
        }

        application.Run();
        trayIcon.Dispose();
    }

    private static System.Windows.Forms.ContextMenuStrip BuildContextMenu(WpfApplication application)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => ShowWindow());
        menu.Items.Add("Refresh", null, (_, _) =>
        {
            ShowWindow();
            window?.RefreshNow();
        });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            trayIcon!.Visible = false;
            window?.ClosePermanently();
            application.Shutdown();
        });
        return menu;
    }

    private static void ShowWindow()
    {
        if (window is null)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.RefreshNow();
    }
}
