using System.IO;
using SpotlightSearch.Models;

namespace SpotlightSearch.Search;

/// <summary>
/// Port of Services/Spotlight/SpotlightSystemCatalog.cs — the fixed set of Windows
/// tools, settings pages and power actions Spotlight can reach.
///
/// Targets are constants declared here, never user input: a search query can only
/// select an entry, it can never become the thing that gets executed.
/// </summary>
internal static class SystemCatalog
{
    internal sealed record Entry(
        string Key,
        SpotlightResultKind Kind,
        string Target,
        string Glyph,
        string Keywords,
        bool Confirm = false)
    {
        public SpotlightSearchItem CreateItem()
        {
            int separator = Keywords.IndexOf(';');
            string fallback = separator > 0 ? Keywords[..separator] : Keywords;
            return new SpotlightSearchItem(
                $"system:{Key}", Kind, fallback,
                Kind switch
                {
                    SpotlightResultKind.Settings => "Windows 设置",
                    SpotlightResultKind.SystemAction => "系统操作",
                    _ => "Windows 工具"
                },
                Target,
                Kind == SpotlightResultKind.Application ? Target : null);
        }
    }

    internal static IReadOnlyList<Entry> Entries { get; } = Array.AsReadOnly(new[]
    {
        Tool("registry", "regedit.exe", "注册表;注册表编辑器;registry;regedit", windowsRoot: true),
        Tool("taskManager", "Taskmgr.exe", "任务管理器;task manager;taskmgr"),
        Tool("resourceMonitor", "resmon.exe", "资源监视器;resource monitor;resmon"),
        Tool("controlPanel", "control.exe", "控制面板;control panel"),
        Tool("terminal", "cmd.exe", "命令提示符;命令行;cmd;command prompt;terminal"),
        Tool("powershell", @"WindowsPowerShell\v1.0\powershell.exe", "PowerShell;powershell"),
        Tool("services", "services.msc", "服务;services"),
        Tool("deviceManager", "devmgmt.msc", "设备管理器;device manager;devmgmt"),
        Tool("diskManager", "diskmgmt.msc", "磁盘管理;disk management;diskmgmt"),
        Tool("eventViewer", "eventvwr.msc", "事件查看器;event viewer;eventvwr"),
        Tool("remoteDesktop", "mstsc.exe", "远程桌面;remote desktop;mstsc"),
        Setting("settingsHome", "", "设置;windows 设置;settings"),
        Setting("display", "display", "显示;屏幕;分辨率;亮度;display;screen;resolution;brightness"),
        Setting("sound", "sound", "声音;音量;音频;sound;audio;volume"),
        Setting("bluetooth", "bluetooth", "蓝牙;bluetooth"),
        Setting("wifi", "network-wifi", "wifi;无线;无线网;wi-fi;wireless"),
        Setting("network", "network-status", "网络;internet;network"),
        Setting("vpn", "network-vpn", "vpn"),
        Setting("apps", "appsfeatures", "已安装应用;卸载;installed apps;uninstall"),
        Setting("defaultApps", "defaultapps", "默认应用;default apps"),
        Setting("startup", "startupapps", "启动项;开机启动;startup apps"),
        Setting("storage", "storagesense", "存储;磁盘空间;storage;disk space"),
        Setting("power", "powersleep", "电源;睡眠;电池;power;sleep;battery"),
        Setting("notifications", "notifications", "通知;notifications"),
        Setting("personalization", "personalization", "个性化;主题;壁纸;背景;personalization;themes;background"),
        Setting("taskbar", "taskbar", "任务栏;taskbar"),
        Setting("dateTime", "dateandtime", "日期和时间;时间;date and time;clock"),
        Setting("language", "regionlanguage", "语言;区域;language;region"),
        Setting("accounts", "yourinfo", "账户;用户;accounts;user"),
        Setting("privacy", "privacy", "隐私;privacy"),
        Setting("update", "windowsupdate", "Windows 更新;更新;windows update"),
        Setting("recovery", "recovery", "恢复;重置;recovery;reset pc"),
        Setting("about", "about", "关于;系统信息;about;system information"),
        new Entry("recycleBin", SpotlightResultKind.SystemAction, "shell:RecycleBinFolder", "\uE74D",
            "回收站;清空回收站;recycle bin;trash"),
        Action("lock", "\uE72E", "锁定;锁屏;lock;lock computer", confirm: false),
        Action("restart", "\uE777", "重启;重新启动;restart;reboot"),
        Action("shutdown", "\uE7E8", "关机;关闭计算机;shut down;shutdown;power off"),
        Action("signOut", "\uE8AC", "注销;退出登录;sign out;log off;logoff"),
        Action("hibernate", "\uE708", "休眠;hibernate"),
        Action("advancedRestart", "\uE777", "高级启动;重启到高级选项;advanced startup;advanced boot")
    });

    private static readonly Dictionary<string, Entry> ByTarget =
        Entries.ToDictionary(entry => entry.Target, StringComparer.OrdinalIgnoreCase);

    internal static Entry? Find(string target) =>
        target != null && ByTarget.TryGetValue(target, out var entry) ? entry : null;

    internal static bool IsKnownTarget(SpotlightSearchItem item) => Find(item.Target)?.Kind == item.Kind;

    internal static bool RequiresConfirmation(SpotlightSearchItem item) =>
        Find(item.Target) is { } entry && entry.Kind == item.Kind && entry.Confirm;

    private static Entry Tool(string key, string file, string keywords, bool windowsRoot = false) =>
        new(key, SpotlightResultKind.Application, Path.Combine(
            windowsRoot ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : Environment.SystemDirectory,
            file), "\uE713", keywords);

    private static Entry Setting(string key, string page, string keywords) =>
        new(key, SpotlightResultKind.Settings, "ms-settings:" + page, "\uE713", keywords);

    private static Entry Action(string key, string glyph, string keywords, bool confirm = true) =>
        new(key, SpotlightResultKind.SystemAction, "spotlight-action:" + key, glyph, keywords, confirm);
}
