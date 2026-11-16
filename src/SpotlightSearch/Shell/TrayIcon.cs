using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SpotlightSearch.Interop;

namespace SpotlightSearch.Shell;

/// <summary>
/// Tray presence is not decoration: this app has no taskbar button and no main
/// window, so without a tray icon there is no way to quit it.
///
/// Drawing types are fully qualified on purpose — this file sits in a project that
/// references both WPF (System.Windows.Media) and WinForms (System.Drawing), and
/// names like Color/Pen exist in both.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public TrayIcon(Action show, Action settings, Action clearHistory, Action exit, Func<string> hotkeyText)
    {
        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("打开搜索", null, (_, _) => show());
        menu.Items.Add(showItem);
        menu.Items.Add("设置…", null, (_, _) => settings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("清空搜索历史", null, (_, _) => clearHistory());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _notifyIcon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "Spotlight 搜索",
            ContextMenuStrip = menu,
            Visible = true
        };

        // The current hotkey rides along in the menu — Flow.Launcher and WinShun
        // both surface it so nobody has to open settings to remember the chord.
        void RefreshLabels()
        {
            string hotkey = hotkeyText();
            showItem.Text = string.IsNullOrEmpty(hotkey) ? "打开搜索" : $"打开搜索（{hotkey}）";
            _notifyIcon.Text = string.IsNullOrEmpty(hotkey) ? "Spotlight 搜索" : $"Spotlight 搜索（{hotkey}）";
        }
        menu.Opening += (_, _) => RefreshLabels();
        RefreshLabels();

        _notifyIcon.DoubleClick += (_, _) => show();
    }

    public void ShowBalloon(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(4000);
    }

    private static System.Drawing.Icon BuildIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);

            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 3.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawEllipse(pen, 4.5f, 4.5f, 15f, 15f);
            graphics.DrawLine(pen, 18f, 18f, 26.5f, 26.5f);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
