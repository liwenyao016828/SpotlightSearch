using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SpotlightSearch.Input;
using SpotlightSearch.Search;
using SpotlightSearch.Settings;
using SpotlightSearch.Shell;
using SpotlightSearch.Windows;

namespace SpotlightSearch;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\SpotlightSearch.SingleInstance";
    private const string ShowSignalName = @"Local\SpotlightSearch.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private Thread? _signalThread;
    private SettingsStore? _settingsStore;
    private UsageStore? _usage;
    private SearchService? _search;
    private SpotlightWindow? _window;
    private HotkeyManager? _hotkey;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        Trace($"startup createdNew={createdNew}");

        if (!createdNew)
        {
            // Already running: ask that instance to show itself, then get out of the way.
            _showSignal.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFailure("AppDomain", args.ExceptionObject as Exception);

        _settingsStore = new SettingsStore();
        _usage = new UsageStore { Enabled = _settingsStore.Current.HistoryEnabled };
        _search = new SearchService(_usage, _settingsStore);
        _window = new SpotlightWindow(_search, _usage, _settingsStore);

        _hotkey = new HotkeyManager(
            toggle: () => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ShowWindow)),
            close: () => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _window?.HideSpotlight())),
            isWindowOpen: () => _window?.IsOpen == true);

        _tray = new TrayIcon(
            show: () => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ShowWindow)),
            settings: () => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(OpenSettings)),
            clearHistory: () => _usage?.Clear(),
            exit: () => Dispatcher.BeginInvoke(new Action(ExitApplication)),
            hotkeyText: () => string.IsNullOrWhiteSpace(_settingsStore.Current.Hotkey)
                ? HotkeySpec.Default.Text
                : _settingsStore.Current.Hotkey);

        _settingsStore.Changed += OnSettingsChanged;
        ApplyHotkey();

        // Build the app index in the background so the first hotkey press is instant.
        _ = _search.WarmupAsync();

        StartSignalListener();

        // Optional CLI: --show / --query "x" / --ai / --settings.
        string? initialQuery = ParseArguments(e.Args, out bool showAtStartup, out bool startInAi, out bool openSettings);
        if (openSettings)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(OpenSettings));
        }
        else if (showAtStartup)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => _window?.ShowSpotlight(initialQuery, startInAi)));
        }
    }

    private static string? ParseArguments(string[] args, out bool show, out bool aiMode, out bool settings)
    {
        show = false;
        aiMode = false;
        settings = false;
        string? query = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--show":
                    show = true;
                    break;
                case "--ai":
                    aiMode = true;
                    show = true;
                    break;
                case "--settings":
                    settings = true;
                    break;
                case "--query" when i + 1 < args.Length:
                    show = true;
                    query = args[++i];
                    break;
            }
        }

        return query;
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.ShowSpotlight();
    }

    private void OpenSettings()
    {
        _window?.OpenSettings();
    }

    private void ApplyHotkey()
    {
        if (_hotkey == null || _settingsStore == null) return;

        var spec = HotkeySpec.TryParse(_settingsStore.Current.Hotkey, out var parsed)
            ? parsed
            : HotkeySpec.Default;

        _hotkey.Register(spec);

        if (_hotkey.IsRegistered) return;

        _tray?.ShowBalloon($"{spec.Text} 不可用",
            $"热键注册失败（Win32={_hotkey.LastError}）。请从托盘菜单打开，或在设置里换一个组合。");
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_usage != null && _settingsStore != null) _usage.Enabled = _settingsStore.Current.HistoryEnabled;
        ApplyHotkey();
        _window?.ApplySettings();
    }

    private void StartSignalListener()
    {
        _signalThread = new Thread(() =>
        {
            while (true)
            {
                if (_showSignal?.WaitOne() != true) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ShowWindow));
            }
        })
        {
            IsBackground = true,
            Name = "Spotlight show signal"
        };
        _signalThread.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A search UI must not take the whole process down because one provider threw.
        LogFailure("Dispatcher", e.Exception);
        e.Handled = true;
    }

    /// <summary>Crash log for a windowless tray app — otherwise failures are invisible.</summary>
    internal static void LogFailure(string source, Exception? exception)
    {
        if (exception == null) return;
        Trace($"{source}: {exception}");
    }

    internal static void Trace(string message)
    {
        try
        {
            string directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotlightSearch");
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(directory, "error.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never throw.
        }
    }

    private void ExitApplication()
    {
        _hotkey?.Dispose();
        _hotkey = null;

        _tray?.Dispose();
        _tray = null;

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Set();
        _showSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
