using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using SpotlightSearch.Interop;

namespace SpotlightSearch.Input;

/// <summary>
/// The global hotkey, two ways.
///
/// RegisterHotKey is the clean path. It fails with ERROR_HOTKEY_ALREADY_REGISTERED
/// (1409) when another app owns the combination — very common, because PowerToys and
/// several IMEs take Alt+Space. In that case we fall back to a low-level keyboard
/// hook, which is what the original V-Notch controller does.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0x564E;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly Action _toggle;
    private readonly Action _close;
    private readonly Func<bool> _isWindowOpen;

    private HwndSource? _source;
    private LowLevelKeyboardHook? _hook;
    private bool _disposed;

    public HotkeyManager(Action toggle, Action close, Func<bool> isWindowOpen)
    {
        _toggle = toggle;
        _close = close;
        _isWindowOpen = isWindowOpen;
    }

    public HotkeySpec Spec { get; private set; } = HotkeySpec.Default;

    public bool IsRegistered => _source != null;

    public bool UsingFallbackHook => _hook != null;

    public int LastError { get; private set; }

    public void Register(HotkeySpec spec)
    {
        Unregister();
        Spec = spec;

        var parameters = new HwndSourceParameters("SpotlightSearch.HotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        if (NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, spec.Modifiers, spec.VirtualKey))
            return;

        LastError = Marshal.GetLastWin32Error();
        if (LastError != ErrorHotkeyAlreadyRegistered) return;

        var hook = new LowLevelKeyboardHook(spec, _toggle, _close, _isWindowOpen);
        if (hook.Install())
        {
            _hook = hook;
            return;
        }

        hook.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotkey || wParam.ToInt64() != HotkeyId) return IntPtr.Zero;

        handled = true;
        // Never do activation work inside the native message callback.
        _source?.Dispatcher.BeginInvoke(DispatcherPriority.Input, _toggle);
        return IntPtr.Zero;
    }

    public void Unregister()
    {
        _hook?.Dispose();
        _hook = null;

        if (_source == null) return;
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }
}

/// <summary>
/// Minimal WH_KEYBOARD_LL hook on its own STA thread. Only used as a fallback, so it
/// stays deliberately small: one combination, one state machine.
/// </summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    private const uint StaleKeyDownMs = 500;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkLWin = 0x5B;

    private readonly HotkeySpec _spec;
    private readonly Action _toggle;
    private readonly Action _close;
    private readonly Func<bool> _isWindowOpen;

    private NativeMethods.LowLevelKeyboardProc? _callback;
    private IntPtr _handle;
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private volatile bool _disposed;
    private byte _keyDown;
    private uint _lastKeyTime;

    public LowLevelKeyboardHook(HotkeySpec spec, Action toggle, Action close, Func<bool> isWindowOpen)
    {
        _spec = spec;
        _toggle = toggle;
        _close = close;
        _isWindowOpen = isWindowOpen;
    }

    public bool Install()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = "Spotlight hotkey hook"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        return ready.Task.GetAwaiter().GetResult();
    }

    private void Run(TaskCompletionSource<bool> ready)
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _callback = HookCallback;
            _handle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WhKeyboardLl, _callback, NativeMethods.GetModuleHandle(null), 0);

            bool installed = _handle != IntPtr.Zero;
            ready.TrySetResult(installed);
            if (installed) Dispatcher.Run();
        }
        catch (Exception)
        {
            ready.TrySetResult(false);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_disposed)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
            int message = (int)wParam;
            bool isKeyDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            bool isKeyUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;

            if (data.VkCode == NativeMethods.VkEscape && _isWindowOpen() && isKeyDown)
            {
                _close();
                return (IntPtr)1;
            }

            // Swallow the key-up that follows a swallowed hotkey, otherwise the
            // focused app receives a stray key press.
            if (_keyDown != 0 && data.VkCode == _spec.VirtualKey && isKeyUp)
            {
                _keyDown = 0;
                return (IntPtr)1;
            }

            if (data.VkCode == _spec.VirtualKey && isKeyDown && ModifiersHeld(data.Flags))
            {
                bool dispatch = _keyDown == 0 || data.Time - _lastKeyTime > StaleKeyDownMs;
                _keyDown = 1;
                _lastKeyTime = data.Time;
                if (dispatch) _toggle();
                return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    private bool ModifiersHeld(uint flags)
    {
        bool alt = (flags & NativeMethods.LlkhfAltDown) != 0;
        if (((_spec.Modifiers & HotkeySpec.ModAlt) != 0) != alt) return false;

        if (((_spec.Modifiers & HotkeySpec.ModControl) != 0) != IsDown(VkControl)) return false;
        if (((_spec.Modifiers & HotkeySpec.ModShift) != 0) != IsDown(VkShift)) return false;
        if (((_spec.Modifiers & HotkeySpec.ModWin) != 0) != IsDown(VkLWin)) return false;

        return true;
    }

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var dispatcher = _dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            ReleaseHook();
            return;
        }

        try
        {
            dispatcher.Invoke(() =>
            {
                ReleaseHook();
                dispatcher.InvokeShutdown();
            }, DispatcherPriority.Send);
        }
        catch (Exception)
        {
            try { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            catch (Exception) { /* already shutting down */ }
        }
    }

    private void ReleaseHook()
    {
        if (_handle == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
    }
}
