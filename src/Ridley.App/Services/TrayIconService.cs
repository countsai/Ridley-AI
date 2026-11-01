using System.Drawing;
using System.Windows.Forms;

namespace Ridley.App.Services;

/// <summary>System tray presence (WinForms NotifyIcon; no extra dependency).</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIconService(Action showWindow, Action exitApplication)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Ridley", null, (_, _) => showWindow());
        menu.Items.Add("Exit", null, (_, _) => exitApplication());
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Ridley",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => showWindow();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
