using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SpotlightSearch.Interop;
using SpotlightSearch.Models;
using SpotlightSearch.Search;
using SpotlightSearch.Settings;
using SpotlightSearch.ViewModels;

namespace SpotlightSearch.Windows;

/// <summary>
/// Port of Windows/SpotlightWindow.xaml.cs — the shell, the search surface and the
/// keyboard model. The notch morphing, liquid glass and audio cues from the original
/// are not part of this extraction; everything that shapes the search experience is.
/// </summary>
public partial class SpotlightWindow : Window
{
    private static readonly TimeSpan FailureDisplayTime = TimeSpan.FromMilliseconds(2800);

    private readonly SearchService _search;
    private readonly UsageStore _usage;
    private readonly SettingsStore _settingsStore;
    private readonly SpotlightViewModel _viewModel;

    private CancellationTokenSource? _searchDebounce;
    private DispatcherTimer? _caretTimer;
    private DispatcherTimer? _failureTimer;

    private Border? _selectionGlide;
    private TranslateTransform? _glideTransform;
    private bool _glideVisible;
    private bool _glideUpdateQueued;

    private bool _contentShown;
    private bool _caretVisible;
    private int _suppressHide;
    private bool _isClosing;
    private DateTime _shownAtUtc;

    internal SpotlightWindow(SearchService search, UsageStore usage, SettingsStore settingsStore)
    {
        _search = search;
        _usage = usage;
        _settingsStore = settingsStore;
        _viewModel = new SpotlightViewModel(search, usage, Dispatcher);

        InitializeComponent();

        Opacity = 1;
        ResultsList.ItemsSource = _viewModel.Results;
        _viewModel.ResultsPublished += OnResultsPublished;

        Deactivated += Window_Deactivated;
        Closed += (_, _) => _isClosing = true;

        ApplySettings();
    }

    internal SpotlightViewModel ViewModel => _viewModel;

    internal bool IsOpen => IsVisible;

    internal void ApplySettings()
    {
        var settings = _settingsStore.Current;
        if (!_aiMode && string.IsNullOrEmpty(SearchBox.Text))
        {
            PlaceholderText.Text = "搜索应用、文件、设置";
        }
        RefreshStatus();
    }

    // ---- show / hide --------------------------------------------------------

    internal void Toggle()
    {
        if (IsVisible) HideSpotlight();
        else ShowSpotlight();
    }

    internal void ShowSpotlight(string? initialQuery = null, bool? aiMode = null)
    {
        if (_isClosing) return;

        _suppressHide = 0;
        ClearLaunchFailure();
        ResetSelectionGlide();

        bool wantAi = aiMode ?? _settingsStore.Current.OpenInAiMode;
        if (wantAi != _aiMode) SetAiMode(wantAi, animate: false);

        if (!string.IsNullOrEmpty(initialQuery))
        {
            SearchBox.Text = initialQuery;
            SearchBox.CaretIndex = initialQuery.Length;
        }
        else if (!_aiMode)
        {
            // Plain search opens as a bare bar — no recents tray, no hint card.
            // The expanded box below is reserved for AI mode and real results.
            _viewModel.Reset();
            SetContentShown(false);
        }

        // Stamp *before* Show(): with ShowActivated=false the window is first
        // shown deactivated, which raises Deactivated synchronously. Stamping
        // afterwards left the grace window comparing against a stale timestamp
        // and the panel hid itself the instant it appeared.
        _shownAtUtc = DateTime.UtcNow;

        // Belt and braces: while the window is coming up no deactivation may
        // hide it, no matter how long activation takes.
        _suppressHide++;
        try
        {
            PositionOnActiveScreen();
            Show();
            ForceForeground();
            PlayEntrance();
            SearchBox.Focus();
            UpdateGlowingCaret();
            RefreshStatus();
        }
        finally
        {
            _suppressHide--;
        }
    }

    internal void HideSpotlight()
    {
        if (!IsVisible) return;

        _viewModel.CancelPendingSearch();
        CancelAiRequest();
        CancelSearchDebounce();
        SearchBox.Text = string.Empty;
        _viewModel.Reset();
        Hide();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_suppressHide > 0) return;
        // The deactivation that fires while the window is still coming up is noise.
        if ((DateTime.UtcNow - _shownAtUtc).TotalMilliseconds < 180) return;
        HideSpotlight();
    }

    private void PositionOnActiveScreen()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var dpi = VisualTreeHelper.GetDpi(this);

        double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        var workArea = screen.WorkingArea;
        double physicalWidth = Width * scaleX;

        Left = (workArea.Left + (workArea.Width - physicalWidth) / 2) / scaleX;
        Top = (workArea.Top + Math.Max(40, workArea.Height * 0.12)) / scaleY;
    }

    private void ForceForeground()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            // Windows refuses SetForegroundWindow from a background process.
            // Borrowing the foreground thread's input queue lifts that lock.
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            uint foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
            uint currentThread = NativeMethods.GetCurrentThreadId();

            bool attached = foregroundThread != 0
                && foregroundThread != currentThread
                && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

            try
            {
                if (NativeMethods.IsIconic(handle)) NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
                NativeMethods.ShowWindow(handle, NativeMethods.SwShow);
                NativeMethods.BringWindowToTop(handle);
                NativeMethods.SetForegroundWindow(handle);
            }
            finally
            {
                if (attached) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
        catch (Exception)
        {
            // Activation is best-effort; the window is already visible and topmost.
        }

        Activate();
        Focus();
    }

    private void PlayEntrance()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = ease
        });

        var scale = new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease };
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    // ---- search -------------------------------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearLaunchFailure();

        if (_aiMode)
        {
            RefreshStatus();
            return;
        }

        CancelSearchDebounce();

        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            // Bare bar again — the box below only exists for AI or real results.
            _viewModel.Reset();
            SetContentShown(false);
            return;
        }

        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        _ = RunSearchAsync(SearchBox.Text, cts.Token);
    }

    private async Task RunSearchAsync(string query, CancellationToken token)
    {
        try
        {
            // Small debounce so a fast typist does not start four disk walks.
            await Task.Delay(50, token);
            await _viewModel.SearchAsync(query, _settingsStore.Current.ResultCount);
            if (token.IsCancellationRequested) return;
            UpdateAutocomplete();
            RefreshStatus();
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
    }

    private void CancelSearchDebounce()
    {
        var cts = _searchDebounce;
        _searchDebounce = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private void OnResultsPublished(object? sender, EventArgs e)
    {
        string? selectedId = (ResultsList.SelectedItem as SpotlightSearchItem)?.Id;

        if (_viewModel.Results.Count == 0)
        {
            ResultsList.SelectedIndex = -1;
        }
        else
        {
            int index = 0;
            if (selectedId != null)
            {
                for (int i = 0; i < _viewModel.Results.Count; i++)
                {
                    if (_viewModel.Results[i].Id != selectedId) continue;
                    index = i;
                    break;
                }
            }
            ResultsList.SelectedIndex = index;
        }

        ScheduleGlideUpdate();
        UpdateAutocomplete();
        RefreshStatus();
    }

    private void UpdateAutocomplete()
    {
        if (_aiMode || _viewModel.Results.Count == 0)
        {
            AutocompleteText.Visibility = Visibility.Collapsed;
            return;
        }

        string query = SearchBox.Text;
        string title = _viewModel.Results[0].DisplayTitle;
        if (query.Length == 0 || title.Length <= query.Length
            || !title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
        {
            AutocompleteText.Visibility = Visibility.Collapsed;
            return;
        }

        AutocompleteTypedRun.Text = title[..query.Length];
        AutocompleteSuffixRun.Text = title[query.Length..];
        AutocompleteText.Visibility = Visibility.Visible;
    }

    // ---- content region -----------------------------------------------------

    private void RefreshStatus()
    {
        if (_aiMode) return;

        // Plain search is a bare bar until there is something to show: the
        // expanded box below is an AI-mode thing. Results earn their own space;
        // an empty query or a dead end just stays compact.
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        if (!hasQuery || _viewModel.Results.Count == 0)
        {
            SetContentShown(false);
            return;
        }

        ResultsList.Visibility = Visibility.Visible;
        SetContentShown(true);
    }

    private void SetContentShown(bool shown)
    {
        if (_contentShown == shown) return;
        _contentShown = shown;
        ContentRegion.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) ResultsList.Visibility = Visibility.Collapsed;
    }

    // ---- selection glide ----------------------------------------------------

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ScheduleGlideUpdate();

    private void ScheduleGlideUpdate()
    {
        if (_glideUpdateQueued || _isClosing) return;
        _glideUpdateQueued = true;

        // Loaded priority runs after layout, when containers have real sizes.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _glideUpdateQueued = false;
            if (!_isClosing) UpdateSelectionGlide();
        });
    }

    private void UpdateSelectionGlide()
    {
        if (!EnsureGlideParts()) return;

        var selected = ResultsList.SelectedItem as SpotlightSearchItem;
        if (selected == null
            || !ResultsList.IsVisible
            || ResultsList.ItemContainerGenerator.ContainerFromItem(selected) is not ListBoxItem container
            || _selectionGlide!.Parent is not UIElement host)
        {
            HideSelectionGlide();
            return;
        }

        Point position = container.TranslatePoint(new Point(0, 0), host);
        double width = container.ActualWidth;
        double height = container.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            HideSelectionGlide();
            return;
        }

        _selectionGlide.Width = width;
        _selectionGlide.Height = height;
        _glideTransform!.X = position.X;

        if (_glideVisible)
        {
            var glide = new DoubleAnimation(position.Y, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 }
            };
            _glideTransform.BeginAnimation(TranslateTransform.YProperty, glide);
        }
        else
        {
            _glideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _glideTransform.Y = position.Y;
            _selectionGlide.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        }

        _glideVisible = true;
    }

    private void HideSelectionGlide()
    {
        if (_selectionGlide == null || !_glideVisible) return;
        _glideVisible = false;
        _selectionGlide.BeginAnimation(OpacityProperty,
            new DoubleAnimation(_selectionGlide.Opacity, 0, TimeSpan.FromMilliseconds(100)));
    }

    private void ResetSelectionGlide()
    {
        _glideVisible = false;
        if (_selectionGlide == null) return;
        _selectionGlide.BeginAnimation(OpacityProperty, null);
        _selectionGlide.Opacity = 0;
        _glideTransform?.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private bool EnsureGlideParts()
    {
        if (_selectionGlide != null && _glideTransform != null) return true;
        ResultsList.ApplyTemplate();
        _selectionGlide = ResultsList.Template.FindName("SelectionGlide", ResultsList) as Border;
        _glideTransform = ResultsList.Template.FindName("SelectionGlideTransform", ResultsList) as TranslateTransform;
        return _selectionGlide != null && _glideTransform != null;
    }

    // ---- keyboard -----------------------------------------------------------

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // AI mode owns Tab / Enter / Escape entirely.
        if (HandleAiKey(e)) return;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                HideSpotlight();
                break;

            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;

            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;

            case Key.PageUp:
                e.Handled = true;
                MoveSelection(-6);
                break;

            case Key.PageDown:
                e.Handled = true;
                MoveSelection(6);
                break;

            case Key.Enter:
                e.Handled = true;
                if (Keyboard.Modifiers == ModifierKeys.Control) RevealSelected();
                else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) LaunchSelectedElevated();
                else LaunchSelected();
                break;

            default:
                // 1..9 launches the matching row directly.
                if (Keyboard.Modifiers == ModifierKeys.None && e.Key is >= Key.D1 and <= Key.D9)
                {
                    int index = e.Key - Key.D1;
                    if (index < _viewModel.Results.Count)
                    {
                        e.Handled = true;
                        LaunchItem(_viewModel.Results[index]);
                    }
                }
                break;
        }
    }

    private void MoveSelection(int direction)
    {
        if (_viewModel.Results.Count == 0) return;

        int index = ResultsList.SelectedIndex < 0 ? 0 : ResultsList.SelectedIndex + direction;
        index = Math.Clamp(index, 0, _viewModel.Results.Count - 1);

        ResultsList.SelectedIndex = index;
        if (ResultsList.SelectedItem != null) ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    // ---- launching ----------------------------------------------------------

    private void ResultItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SpotlightSearchItem item })
        {
            LaunchItem(item);
            e.Handled = true;
        }
    }

    private void ResultItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: SpotlightSearchItem item } container) return;

        ResultsList.SelectedItem = item;
        ContextMenu menu = BuildResultContextMenu(item);
        menu.PlacementTarget = container;
        menu.Placement = PlacementMode.Bottom;
        menu.Style = (Style)FindResource("SpotlightContextMenuStyle");
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildResultContextMenu(SpotlightSearchItem item)
    {
        var menu = new ContextMenu();
        var itemStyle = (Style)FindResource("SpotlightMenuItemStyle");

        void Add(string header, Action action, bool enabled = true)
        {
            var menuItem = new MenuItem { Header = header, Style = itemStyle, IsEnabled = enabled };
            menuItem.Click += (_, _) => action();
            menu.Items.Add(menuItem);
        }

        Add("打开", () => LaunchItem(item));
        Add("以管理员身份运行", () => LaunchItemElevated(item), Launcher.CanLaunchElevated(item));
        Add("打开文件所在位置", RevealSelected, Launcher.CanReveal(item));

        string? copyable = Launcher.GetCopyableText(item);
        if (copyable != null) Add("复制路径", () => TryCopyToClipboard(copyable));

        if (item.IsRecent)
        {
            menu.Items.Add(new Separator());
            Add("从历史记录中移除", () =>
            {
                _viewModel.RemoveResult(item);
                RefreshStatus();
            });
        }

        return menu;
    }

    private void LaunchSelected()
    {
        if (ResultsList.SelectedItem is SpotlightSearchItem item) LaunchItem(item);
    }

    private void LaunchItem(SpotlightSearchItem item)
    {
        if (item.Kind == SpotlightResultKind.Calculation)
        {
            if (TryCopyToClipboard(item.Target)) HideSpotlight();
            else ShowLaunchFailure(item, "无法复制到剪贴板");
            return;
        }

        if (Launcher.TryLaunch(item))
        {
            _usage.RecordLaunch(item, SearchBox.Text);
            HideSpotlight();
            return;
        }

        ShowLaunchFailure(item, "无法打开（目标可能已被移动或删除）");
    }

    private void LaunchItemElevated(SpotlightSearchItem item)
    {
        if (!Launcher.TryLaunchElevated(item, out bool cancelled))
        {
            if (!cancelled) ShowLaunchFailure(item, "提权启动失败");
            return;
        }

        _usage.RecordLaunch(item, SearchBox.Text);
        HideSpotlight();
    }

    private void LaunchSelectedElevated()
    {
        if (ResultsList.SelectedItem is SpotlightSearchItem item) LaunchItemElevated(item);
    }

    private void RevealSelected()
    {
        if (ResultsList.SelectedItem is not SpotlightSearchItem item) return;
        if (!Launcher.TryReveal(item))
        {
            ShowLaunchFailure(item, "无法在资源管理器中定位");
            return;
        }

        HideSpotlight();
    }

    private void ShowLaunchFailure(SpotlightSearchItem item, string message)
    {
        FailureText.Text = $"{item.DisplayTitle} · {message}";
        FailureBar.Visibility = Visibility.Visible;
        PlayShake();

        _failureTimer?.Stop();
        _failureTimer = new DispatcherTimer { Interval = FailureDisplayTime };
        _failureTimer.Tick += (_, _) => ClearLaunchFailure();
        _failureTimer.Start();
    }

    private void ClearLaunchFailure()
    {
        _failureTimer?.Stop();
        _failureTimer = null;
        FailureBar.Visibility = Visibility.Collapsed;
    }

    private void PlayShake()
    {
        var shake = new DoubleAnimationUsingKeyFrames();
        foreach (var (time, offset) in new[] { (0.0, 0.0), (0.08, -6.0), (0.18, 5.0), (0.28, -3.0), (0.38, 0.0) })
        {
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(offset, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time))));
        }
        ShellShake.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    private static bool TryCopyToClipboard(string text)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(30);
            }
        }
        return false;
    }

    // ---- caret --------------------------------------------------------------

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e) => UpdateGlowingCaret();
    private void SearchBox_LostFocus(object sender, RoutedEventArgs e) => GlowingCaret.Visibility = Visibility.Collapsed;
    private void SearchBox_SelectionChanged(object sender, RoutedEventArgs e) => UpdateGlowingCaret();

    private void UpdateGlowingCaret()
    {
        if (!SearchBox.IsKeyboardFocused)
        {
            GlowingCaret.Visibility = Visibility.Collapsed;
            return;
        }

        Rect caret = SearchBox.GetRectFromCharacterIndex(SearchBox.CaretIndex, trailingEdge: false);
        if (caret.IsEmpty)
        {
            GlowingCaret.Visibility = Visibility.Collapsed;
            return;
        }

        GlowingCaret.Margin = new Thickness(caret.Left + 2, 0, 0, 0);
        GlowingCaret.Visibility = Visibility.Visible;
        _caretVisible = true;

        if (_caretTimer != null) return;
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            GlowingCaret.Opacity = _caretVisible ? 1 : 0;
        };
        _caretTimer.Start();
    }

    private void SearchBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // The system text menu would paint over the dark shell with a light popup.
        e.Handled = true;
    }

    // ---- settings -----------------------------------------------------------

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    internal void OpenSettings()
    {
        _suppressHide++;
        try
        {
            var window = new SettingsWindow(_settingsStore, _usage);
            // WPF refuses Owner on a window that has never been shown — and the
            // tray can open settings while the search panel is still hidden.
            if (IsVisible) window.Owner = this;
            window.ShowDialog();
        }
        finally
        {
            _suppressHide--;
        }

        ApplySettings();
        SearchBox.Focus();
    }
}
