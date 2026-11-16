using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SpotlightSearch.Ai;
using SpotlightSearch.Input;
using SpotlightSearch.Search;
using SpotlightSearch.Settings;

namespace SpotlightSearch.Windows;

/// <summary>
/// Settings dialog. Everything it writes goes through SettingsStore, so the rest of
/// the app only has to react to one change event.
/// </summary>
public partial class SettingsWindow : Window
{
    private const string AutoStartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValue = "SpotlightSearch";

    private readonly SettingsStore _store;
    private readonly UsageStore _usage;
    private readonly AppSettings _draft;

    internal SettingsWindow(SettingsStore store, UsageStore usage)
    {
        _store = store;
        _usage = usage;
        _draft = store.Current.Clone();

        InitializeComponent();
        BuildProviderChips();
        LoadIntoUi();

        ResultCountSlider.ValueChanged += (_, _) => UpdateSliderLabels();
        FileBudgetSlider.ValueChanged += (_, _) => UpdateSliderLabels();
    }

    private void BuildProviderChips()
    {
        foreach (string provider in AiService.Providers)
        {
            var chip = new RadioButton
            {
                Content = provider,
                GroupName = "AiProvider",
                Style = (Style)FindResource("ChipStyle"),
                Tag = provider
            };
            chip.Checked += (_, _) =>
            {
                if (chip.IsChecked != true) return;
                if (ModelBox == null) return;
                if (string.IsNullOrWhiteSpace(ModelBox.Text) || AiService.Providers.Contains(ModelBox.Text))
                {
                    ModelBox.Text = AiService.DefaultModel(provider);
                }
                UpdateKeyHint();
            };
            ProviderPanel.Children.Add(chip);
        }
    }

    private void LoadIntoUi()
    {
        HotkeyBox.Text = _draft.Hotkey;
        AiDefaultCheck.IsChecked = _draft.OpenInAiMode;
        HistoryCheck.IsChecked = _draft.HistoryEnabled;
        FileSearchCheck.IsChecked = _draft.FileSearchEnabled;
        AutoStartCheck.IsChecked = _draft.AutoStart;

        ResultCountSlider.Value = _draft.ResultCount;
        FileBudgetSlider.Value = _draft.FileSearchBudgetMs;

        ModelBox.Text = _draft.AiModel;
        ApiKeyBox.Password = _draft.AiApiKey;
        BaseUrlBox.Text = _draft.AiBaseUrl;

        foreach (var child in ProviderPanel.Children)
        {
            if (child is RadioButton { Tag: string provider } chip)
                chip.IsChecked = provider == _draft.AiProvider;
        }

        UpdateSliderLabels();
        UpdateKeyHint();
    }

    private void UpdateSliderLabels()
    {
        ResultCountLabel.Text = $"结果数量：{(int)ResultCountSlider.Value} 条";
        FileBudgetLabel.Text = $"文件搜索耗时上限：{(int)FileBudgetSlider.Value} ms（越小越省电，越大搜得越全）";
    }

    private void UpdateKeyHint()
    {
        string provider = SelectedProvider();
        KeyHint.Text = provider switch
        {
            "Gemini" => "在 Google AI Studio 创建 API Key。",
            "Claude" => "在 Anthropic Console 创建 API Key。",
            "DeepSeek" => "在 platform.deepseek.com 创建 API Key。",
            "OpenAI" => "在 platform.openai.com 创建 API Key。",
            _ => "填兼容 OpenAI 接口的服务密钥。"
        };
    }

    private string SelectedProvider()
    {
        foreach (var child in ProviderPanel.Children)
        {
            if (child is RadioButton { IsChecked: true, Tag: string provider }) return provider;
        }
        return "DeepSeek";
    }

    // ---- hotkey recorder ----------------------------------------------------
    // WinShun HotkeyRecorder / Flow HotkeyControlDialog idea: focus the box and
    // press the chord — no text to type, no format to remember. Text entry via
    // paste still works as a fallback (the save path re-validates it).

    private bool _recordingHotkey;
    private string _hotkeyBeforeRecording = string.Empty;

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        _hotkeyBeforeRecording = HotkeyBox.Text;
        HotkeyBox.Text = string.Empty;
        HotkeyHint.Text = "按下组合键（需含 Ctrl/Alt/Shift/Win）· Esc 取消 · Backspace 清除";
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Leave no half-recorded chord behind ("Ctrl+…") — fall back to the old one.
        if (_recordingHotkey && !HotkeySpec.TryParse(HotkeyBox.Text, out _))
            HotkeyBox.Text = _hotkeyBeforeRecording;
        _recordingHotkey = false;
        HotkeyHint.Text = "点击输入框后按下组合键即可设置，例如 Alt+Space、Ctrl+Alt+Space。";
    }

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // The recorder owns every key while the box has focus.
        e.Handled = true;

        System.Windows.Input.Key key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var mods = System.Windows.Input.Keyboard.Modifiers;

        if (key == System.Windows.Input.Key.Escape)
        {
            HotkeyBox.Text = _hotkeyBeforeRecording;
            _recordingHotkey = false;
            HotkeyBox.MoveFocus(new System.Windows.Input.TraversalRequest(
                System.Windows.Input.FocusNavigationDirection.Next));
            return;
        }

        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
        {
            HotkeyBox.Text = DescribeModifiers(mods) + "…";
            return;
        }

        if (key == System.Windows.Input.Key.Back && mods == System.Windows.Input.ModifierKeys.None)
        {
            HotkeyBox.Text = string.Empty;
            return;
        }

        string? token = KeyToken(key);
        if (token == null)
        {
            HotkeyHint.Text = "这个键不支持，请换一个。";
            return;
        }

        if (mods == System.Windows.Input.ModifierKeys.None)
        {
            HotkeyHint.Text = "需要至少一个修饰键：Ctrl / Alt / Shift / Win。";
            return;
        }

        if (HotkeySpec.TryParse(DescribeModifiers(mods) + token, out var spec))
        {
            HotkeyBox.Text = spec.Text;
            HotkeyHint.Text = "点击输入框后按下组合键即可设置，例如 Alt+Space、Ctrl+Alt+Space。";
        }
    }

    private static string DescribeModifiers(System.Windows.Input.ModifierKeys mods)
    {
        var parts = new List<string>(4);
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Windows)) parts.Add("Win");
        return parts.Count == 0 ? string.Empty : string.Join("+", parts) + "+";
    }

    /// <summary>WPF key to the token names HotkeySpec.TryParseKey understands.</summary>
    private static string? KeyToken(System.Windows.Input.Key key)
    {
        if (key is >= System.Windows.Input.Key.A and <= System.Windows.Input.Key.Z)
            return ((char)('A' + (key - System.Windows.Input.Key.A))).ToString();
        if (key is >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9)
            return ((char)('0' + (key - System.Windows.Input.Key.D0))).ToString();
        if (key is >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9)
            return ((char)('0' + (key - System.Windows.Input.Key.NumPad0))).ToString();
        if (key is >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24)
            return "F" + (key - System.Windows.Input.Key.F1 + 1);

        return key switch
        {
            System.Windows.Input.Key.Space => "Space",
            System.Windows.Input.Key.Return => "Enter",
            System.Windows.Input.Key.Tab => "Tab",
            System.Windows.Input.Key.Back => "Backspace",
            System.Windows.Input.Key.Insert => "Insert",
            System.Windows.Input.Key.Delete => "Delete",
            System.Windows.Input.Key.Home => "Home",
            System.Windows.Input.Key.End => "End",
            System.Windows.Input.Key.PageUp => "PageUp",
            System.Windows.Input.Key.Next => "PageDown",
            System.Windows.Input.Key.Left => "Left",
            System.Windows.Input.Key.Up => "Up",
            System.Windows.Input.Key.Right => "Right",
            System.Windows.Input.Key.Down => "Down",
            System.Windows.Input.Key.OemTilde => "`",
            System.Windows.Input.Key.OemMinus => "-",
            System.Windows.Input.Key.OemPlus => "=",
            System.Windows.Input.Key.OemOpenBrackets => "[",
            System.Windows.Input.Key.OemCloseBrackets => "]",
            System.Windows.Input.Key.Oem1 => ";",
            System.Windows.Input.Key.Oem7 => "'",
            System.Windows.Input.Key.OemComma => ",",
            System.Windows.Input.Key.OemPeriod => ".",
            System.Windows.Input.Key.OemQuestion => "/",
            System.Windows.Input.Key.Oem5 => "\\",
            _ => null
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!HotkeySpec.TryParse(HotkeyBox.Text, out var hotkey))
        {
            StatusLabel.Text = "快捷键格式不对，例如 Alt+Space。";
            return;
        }

        _draft.Hotkey = hotkey.Text;
        _draft.OpenInAiMode = AiDefaultCheck.IsChecked == true;
        _draft.HistoryEnabled = HistoryCheck.IsChecked == true;
        _draft.FileSearchEnabled = FileSearchCheck.IsChecked == true;
        _draft.AutoStart = AutoStartCheck.IsChecked == true;
        _draft.ResultCount = (int)ResultCountSlider.Value;
        _draft.FileSearchBudgetMs = (int)FileBudgetSlider.Value;
        _draft.AiProvider = SelectedProvider();
        _draft.AiModel = ModelBox.Text.Trim();
        _draft.AiBaseUrl = BaseUrlBox.Text.Trim();
        _draft.AiApiKey = ApiKeyBox.Password;

        _usage.Enabled = _draft.HistoryEnabled;
        ApplyAutoStart(_draft.AutoStart);
        _store.Save(_draft);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _usage.Clear();
        StatusLabel.Text = "历史已清空。";
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        defaults.Normalize();

        HotkeyBox.Text = defaults.Hotkey;
        AiDefaultCheck.IsChecked = defaults.OpenInAiMode;
        HistoryCheck.IsChecked = defaults.HistoryEnabled;
        FileSearchCheck.IsChecked = defaults.FileSearchEnabled;
        AutoStartCheck.IsChecked = defaults.AutoStart;
        ResultCountSlider.Value = defaults.ResultCount;
        FileBudgetSlider.Value = defaults.FileSearchBudgetMs;
        ModelBox.Text = defaults.AiModel;
        BaseUrlBox.Text = defaults.AiBaseUrl;
        ApiKeyBox.Password = string.Empty;

        foreach (var child in ProviderPanel.Children)
        {
            if (child is RadioButton { Tag: string provider } chip)
                chip.IsChecked = provider == defaults.AiProvider;
        }

        StatusLabel.Text = "已填回默认值，点保存后生效。";
    }

    internal static void ApplyAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, writable: true);
            if (key == null) return;

            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(AutoStartValue, $"\"{exe}\"");
            }
            else if (key.GetValue(AutoStartValue) != null)
            {
                key.DeleteValue(AutoStartValue, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // Locked-down profiles simply do not get autostart.
        }
    }
}
