using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SpotlightSearch.Ai;
using SpotlightSearch.Controls;

namespace SpotlightSearch.Windows;

/// <summary>
/// Port of the AI surface (SpotlightWindow.Ai.cs / .AiMotion.cs / .AiToolbar.cs).
///
/// Same layout and same streaming model: user turn on the right, assistant answer as
/// a flat markdown block with a per-message copy button, a stop button while the
/// request is live, and token usage when the provider reports it. Motion is reduced to
/// a fade-in — the original's spring/fade word reveal is not reproduced.
/// </summary>
public partial class SpotlightWindow
{
    private static readonly TimeSpan AiRequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly Brush ChatIconBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200));
    private static readonly Brush MutedLabelBrush = new SolidColorBrush(Color.FromRgb(140, 149, 158));

    private readonly AiService _aiService = new();
    private readonly List<AiMessage> _aiHistory = [];
    private readonly List<AiConversation> _aiConversations = [];

    private bool _aiMode;
    private bool _aiHistoryOpen;
    private CancellationTokenSource? _aiRequest;
    private RichTextBox? _liveView;
    private DispatcherTimer? _revealTimer;
    private DispatcherTimer? _progressTimer;
    private string _streamText = string.Empty;
    private AiUsage _aiUsage = AiUsage.Empty;
    private string _aiStatusText = string.Empty;
    private bool _aiStatusIsError;
    private string _aiDiagnostic = string.Empty;
    private AiConversation? _currentConversation;

    private sealed class AiConversation
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = string.Empty;
        public List<AiMessage> Messages { get; } = [];
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    private bool IsSearchBoxFocused =>
        SearchBox.IsKeyboardFocused || SearchBox.IsKeyboardFocusWithin;

    private bool HandleAiKey(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && IsSearchBoxFocused)
        {
            if (!e.IsRepeat) SetAiMode(!_aiMode, animate: true);
            e.Handled = true;
            return true;
        }

        if (!_aiMode) return false;

        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                if (!e.IsRepeat) _ = SendAiAsync();
                return true;

            case Key.Escape:
                e.Handled = true;
                HideSpotlight();
                return true;
        }

        return false;
    }

    private void SetAiMode(bool enabled, bool animate)
    {
        if (_aiMode == enabled) return;

        CancelAiRequest();
        _aiMode = enabled;
        CloseAiHistory();

        AiPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        AutocompleteText.Visibility = Visibility.Collapsed;
        AiGlowBorder.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        AiGlowBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(enabled ? 1 : 0, TimeSpan.FromMilliseconds(200)));

        if (enabled)
        {
            PlaceholderText.Text = "问点什么…";
            EscBadgeText.Text = "TAB · 搜索";
            RefreshAiPanel();
            SetContentShown(true);
            if (animate) PlayFadeIn(AiPanel);
        }
        else
        {
            PlaceholderText.Text = "搜索应用、文件、设置";
            EscBadgeText.Text = "TAB · AI";
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _viewModel.Reset();
                SetContentShown(false);
            }
            else
            {
                _ = _viewModel.SearchAsync(SearchBox.Text, _settingsStore.Current.ResultCount);
                RefreshStatus();
            }
        }

        SearchBox.Focus();
        UpdateGlowingCaret();
    }

    private static void PlayFadeIn(UIElement element)
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = ease
        });
        var translate = new TranslateTransform(0, 8);
        element.RenderTransform = translate;
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    private void RefreshAiPanel()
    {
        ResultsList.Visibility = Visibility.Collapsed;
        AiPanel.Visibility = Visibility.Visible;
        SetContentShown(true);

        AiModeLabel.Text = AiService.Describe(_settingsStore.Current);
        // The pretty name drops snapshot suffixes; the raw id stays on hover.
        AiModelPill.ToolTip = string.IsNullOrWhiteSpace(_settingsStore.Current.AiModel)
            ? null
            : _settingsStore.Current.AiModel;
        UpdateAiWelcome();
        UpdateAiMetadata();
        UpdateAiActionState();
        RenderAiStatus();
    }

    // ---- conversation rendering --------------------------------------------

    private void RenderAiHistory()
    {
        AiTranscript.Children.Clear();
        _liveView = null;
        foreach (var message in _aiHistory) AddAiMessage(message, animate: false);
        AiTranscriptScroll.ScrollToEnd();
        UpdateAiWelcome();
        UpdateAiMetadata();
    }

    private void AddUserMessage(string text)
    {
        var container = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(40, 4, 0, 10),
            MaxWidth = 540
        };

        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = "你",
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = MutedLabelBrush,
            Margin = new Thickness(0, 0, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Right
        });

        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = ChatIconBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            TextAlignment = TextAlignment.Right
        });

        container.Children.Add(stack);
        AiTranscript.Children.Add(container);
        AnimateArrival(container);
    }

    private RichTextBox AddAssistantMessage(string text)
    {
        var font = (FontFamily)FindResource("UiFont");

        var card = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 4, 0, 8),
            Margin = new Thickness(0, 6, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var stack = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new TextBlock
        {
            Text = "\u2726",
            FontFamily = font,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = ChatIconBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        brand.Children.Add(new TextBlock
        {
            Text = _settingsStore.Current.AiProvider,
            FontFamily = font,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = ChatIconBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(brand);

        var view = new RichTextBox
        {
            Document = MarkdownRenderer.Render(text, font),
            IsReadOnly = true,
            IsDocumentEnabled = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = font,
            FontWeight = FontWeights.Bold,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var copyButton = new Button
        {
            Style = (Style)FindResource("AiToolbarStyle"),
            Width = 24,
            Height = 24,
            ToolTip = "复制这条回答",
            Content = new TextBlock
            {
                Text = "\uE8C8",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 11,
                FontWeight = FontWeights.Normal,
                Foreground = ChatIconBrush
            }
        };
        Grid.SetColumn(copyButton, 2);
        copyButton.Click += async (_, _) =>
        {
            if (!TryCopyToClipboard(text)) return;
            copyButton.Content = new TextBlock
            {
                Text = "\u2713",
                FontFamily = font,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = ChatIconBrush
            };
            await Task.Delay(1600);
            copyButton.Content = new TextBlock
            {
                Text = "\uE8C8",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 11,
                FontWeight = FontWeights.Normal,
                Foreground = ChatIconBrush
            };
        };

        header.Children.Add(copyButton);
        stack.Children.Add(header);
        stack.Children.Add(view);
        card.Child = stack;

        AiTranscript.Children.Add(card);
        AnimateArrival(card);
        return view;
    }

    private void AddAiMessage(AiMessage message, bool animate)
    {
        if (message.Role == "user")
        {
            AddUserMessage(message.Content);
            return;
        }

        var view = AddAssistantMessage(message.Content);
        if (!animate) view.BeginAnimation(OpacityProperty, null);
    }

    private static void AnimateArrival(UIElement element)
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = ease
        });
        var translate = new TranslateTransform(0, 8);
        element.RenderTransform = translate;
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    private void UpdateAiWelcome()
    {
        AiWelcome.Visibility = _aiHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AiWelcomeTitle.Text = "有什么可以帮你？";
        AiWelcomeHint.Text = AiService.IsConfigured(_settingsStore.Current)
            ? "输入问题后按 Enter，Tab 返回搜索"
            : "还没有配置模型，点右上角齿轮填写 API Key";

        AiPromptOne.Content = "解释一下这段报错";
        AiPromptTwo.Content = "帮我把这段话写得更简洁";
    }

    private void UpdateAiMetadata()
    {
        int chars = _aiHistory.Sum(message => message.Content.Length);
        AiMetadata.Text = _aiHistory.Count == 0 ? string.Empty : $"{_aiHistory.Count} 条 · {chars} 字";
        AiCopyButton.IsEnabled = _aiHistory.Count > 0;
    }

    private void UpdateAiActionState()
    {
        AiStopButton.Visibility = _aiRequest != null ? Visibility.Visible : Visibility.Collapsed;
        AiUsageText.Visibility = _aiUsage.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiUsageText.Text = _aiUsage.Total > 0 ? $"≈ {_aiUsage.Total:N0} tokens" : string.Empty;
    }

    private void RenderAiStatus()
    {
        string text = _aiStatusText + (_aiDiagnostic.Length > 0 ? $" ({_aiDiagnostic})" : string.Empty);
        AiStatus.Text = text;
        AiStatus.Tag = _aiStatusIsError ? "error" : null;
        AiStatus.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        UpdateAiActionState();
    }

    private void SetAiStatus(string text, bool isError = false, string diagnostic = "")
    {
        _aiStatusText = text;
        _aiStatusIsError = isError;
        _aiDiagnostic = diagnostic;
        RenderAiStatus();
    }

    // ---- send / stream ------------------------------------------------------

    private async Task SendAiAsync()
    {
        if (!_aiMode || _aiRequest != null) return;

        string prompt = SearchBox.Text.Trim();
        if (prompt.Length == 0) return;

        var settings = _settingsStore.Current;
        if (!AiService.IsConfigured(settings))
        {
            SetAiStatus("请先在设置里填写 API Key 和模型。", isError: true);
            return;
        }

        if (prompt.Length > 16000)
        {
            SetAiStatus("问题太长了，请拆成几段。", isError: true);
            return;
        }

        CloseAiHistory();

        _aiHistory.Add(new AiMessage("user", prompt));
        AddUserMessage(prompt);
        SearchBox.Clear();
        UpdateAiWelcome();
        UpdateAiMetadata();
        SetAiStatus("等待模型响应…");
        AiTranscriptScroll.ScrollToEnd();

        var cts = new CancellationTokenSource();
        _aiRequest = cts;
        UpdateAiActionState();

        var started = System.Diagnostics.Stopwatch.StartNew();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _progressTimer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_aiRequest, cts)) return;
            if (_aiHistory.Count > 0 && _aiHistory[^1].Role == "assistant") return;
            SetAiStatus($"等待模型响应… {started.Elapsed.TotalSeconds:F0}s");
        };
        _progressTimer.Start();

        _streamText = string.Empty;
        _liveView = null;

        try
        {
            var history = BuildRequestHistory();
            var usage = AiUsage.Empty;

            cts.CancelAfter(AiRequestTimeout);

            await foreach (string delta in _aiService.StreamAsync(settings, history, snapshot =>
                       {
                           usage = snapshot;
                           Dispatcher.BeginInvoke(() =>
                           {
                               if (!ReferenceEquals(_aiRequest, cts)) return;
                               _aiUsage = snapshot;
                               UpdateAiActionState();
                           });
                       }, cts.Token))
            {
                if (!ReferenceEquals(_aiRequest, cts) || cts.IsCancellationRequested) return;

                cts.CancelAfter(AiRequestTimeout);
                _streamText += delta;

                if (_liveView == null)
                {
                    _aiHistory.Add(new AiMessage("assistant", string.Empty) { IsIncomplete = true });
                    _liveView = AddAssistantMessage(string.Empty);
                    StartRevealTimer();
                    SetAiStatus("正在回答…");
                }

                UpdateAiMetadata();
            }

            StopRevealTimer();
            RenderStreamText();

            if (_aiHistory.Count > 0 && _aiHistory[^1].Role == "assistant")
            {
                _aiHistory[^1] = new AiMessage("assistant", _streamText);
            }

            _aiUsage = usage;
            SetAiStatus(_aiHistory.Count > 0 ? string.Empty : "没有收到内容，请重试。");
        }
        catch (Exception ex)
        {
            StopRevealTimer();

            if (!ReferenceEquals(_aiRequest, cts)) return;

            // A failed turn is retryable: drop the user message and restore the draft.
            if (_aiHistory.Count > 0 && _aiHistory[^1].Role == "user")
            {
                _aiHistory.RemoveAt(_aiHistory.Count - 1);
                if (SearchBox.Text.Length == 0) SearchBox.Text = prompt;
                if (AiTranscript.Children.Count > 0)
                    AiTranscript.Children.RemoveAt(AiTranscript.Children.Count - 1);
            }

            string message = ex is AiException
                ? ex.Message
                : ex is TimeoutException or OperationCanceledException
                    ? "请求超时或已取消。"
                    : "网络错误，请检查连接后重试。";

            SetAiStatus(message, isError: true);
            RenderAiHistory();
        }
        finally
        {
            _progressTimer?.Stop();
            _progressTimer = null;

            if (ReferenceEquals(_aiRequest, cts))
            {
                _aiRequest = null;
                UpdateAiActionState();
                UpdateAiWelcome();
                UpdateAiMetadata();
                SaveCurrentConversation();
            }

            cts.Dispose();
        }
    }

    private List<AiMessage> BuildRequestHistory()
    {
        var history = new List<AiMessage>();
        foreach (var message in _aiHistory)
        {
            if (message.IsIncomplete) continue;
            history.Add(message);
        }

        // Bound the context, never the visible transcript.
        while (history.Count > 21 || (history.Count > 2 && history.Sum(m => m.Content.Length) > 64000))
        {
            history.RemoveRange(0, Math.Min(2, history.Count));
        }

        return history;
    }

    private void StartRevealTimer()
    {
        if (_revealTimer != null) return;
        _revealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _revealTimer.Tick += (_, _) => RenderStreamText();
        _revealTimer.Start();
    }

    private void StopRevealTimer()
    {
        if (_revealTimer == null) return;
        _revealTimer.Stop();
        _revealTimer = null;
    }

    private void RenderStreamText()
    {
        if (_liveView == null) return;

        double distanceFromBottom = AiTranscriptScroll.ScrollableHeight - AiTranscriptScroll.VerticalOffset;
        _liveView.Document = MarkdownRenderer.Render(_streamText, (FontFamily)FindResource("UiFont"));
        if (distanceFromBottom < 40) AiTranscriptScroll.ScrollToEnd();
    }

    private void CancelAiRequest()
    {
        var cts = _aiRequest;
        if (cts == null) return;

        _aiRequest = null;
        StopRevealTimer();
        cts.Cancel();

        if (_aiHistory.Count > 0 && _aiHistory[^1].Role == "user")
        {
            string prompt = _aiHistory[^1].Content;
            _aiHistory.RemoveAt(_aiHistory.Count - 1);
            if (SearchBox.Text.Length == 0) SearchBox.Text = prompt;
        }

        if (_aiHistory.Count > 0 && _aiHistory[^1].IsIncomplete)
        {
            _aiHistory[^1] = new AiMessage("assistant", _streamText);
        }

        UpdateAiActionState();
        SetAiStatus("已停止。");
        RenderAiHistory();
    }

    // ---- conversation store (session only) ---------------------------------

    private void SaveCurrentConversation()
    {
        if (_aiHistory.Count == 0) return;

        _currentConversation ??= new AiConversation();
        _currentConversation.Messages.Clear();
        _currentConversation.Messages.AddRange(_aiHistory);
        _currentConversation.Title = _aiHistory.FirstOrDefault(m => m.Role == "user")?.Content ?? "新对话";
        if (_currentConversation.Title.Length > 24)
            _currentConversation.Title = _currentConversation.Title[..24] + "…";
        _currentConversation.UpdatedUtc = DateTime.UtcNow;

        if (!_aiConversations.Contains(_currentConversation)) _aiConversations.Insert(0, _currentConversation);
        else
        {
            _aiConversations.Remove(_currentConversation);
            _aiConversations.Insert(0, _currentConversation);
        }
    }

    private void RenderAiConversationList()
    {
        AiHistoryItems.Children.Clear();
        var font = (FontFamily)FindResource("UiFont");

        if (_aiConversations.Count == 0)
        {
            AiHistoryItems.Children.Add(new TextBlock
            {
                Text = "还没有历史对话",
                FontFamily = font,
                FontSize = 12,
                Foreground = MutedLabelBrush,
                Margin = new Thickness(4, 8, 0, 8)
            });
            return;
        }

        foreach (var conversation in _aiConversations)
        {
            var button = new Button
            {
                Style = (Style)FindResource("AiSuggestionStyle"),
                Content = new TextBlock
                {
                    Text = conversation.Title,
                    FontFamily = font,
                    FontSize = 12.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = ChatIconBrush
                },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 1, 0, 1)
            };

            button.Click += (_, _) =>
            {
                _aiHistory.Clear();
                _aiHistory.AddRange(conversation.Messages);
                _currentConversation = conversation;
                CloseAiHistory();
                RenderAiHistory();
                SetAiStatus(string.Empty);
                AiTranscriptScroll.ScrollToEnd();
            };

            AiHistoryItems.Children.Add(button);
        }
    }

    private void CloseAiHistory()
    {
        if (!_aiHistoryOpen) return;
        _aiHistoryOpen = false;
        AiHistoryPanel.Visibility = Visibility.Collapsed;
        AiWelcome.Visibility = _aiHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AiTranscriptScroll.Visibility = Visibility.Visible;
    }

    // ---- click handlers -----------------------------------------------------

    private void AiToggle_Click(object sender, MouseButtonEventArgs e)
    {
        SetAiMode(!_aiMode, animate: true);
        e.Handled = true;
    }

    private void AiBack_Click(object sender, RoutedEventArgs e)
    {
        if (_aiHistoryOpen)
        {
            CloseAiHistory();
            RefreshAiPanel();
            return;
        }

        SetAiMode(false, animate: true);
    }

    private void AiCopy_Click(object sender, RoutedEventArgs e)
    {
        string text = string.Join("\n\n", _aiHistory.Select(message => message.Content));
        if (text.Length == 0) return;
        SetAiStatus(TryCopyToClipboard(text) ? "已复制全部对话。" : "复制失败，剪贴板被占用。");
    }

    private void AiHistory_Click(object sender, RoutedEventArgs e)
    {
        _aiHistoryOpen = !_aiHistoryOpen;
        if (_aiHistoryOpen)
        {
            RenderAiConversationList();
            AiHistoryPanel.Visibility = Visibility.Visible;
            AiTranscriptScroll.Visibility = Visibility.Collapsed;
            AiWelcome.Visibility = Visibility.Collapsed;
        }
        else
        {
            CloseAiHistory();
        }
    }

    private void AiNewChat_Click(object sender, RoutedEventArgs e)
    {
        CancelAiRequest();
        SaveCurrentConversation();

        _currentConversation = null;
        _aiHistory.Clear();
        _streamText = string.Empty;
        _aiUsage = AiUsage.Empty;
        CloseAiHistory();
        RenderAiHistory();
        SearchBox.Clear();
        SetAiStatus(string.Empty);
        SearchBox.Focus();
    }

    private void AiSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string prompt }) return;
        SearchBox.Text = prompt;
        SearchBox.CaretIndex = prompt.Length;
        _ = SendAiAsync();
    }

    private void AiStop_Click(object sender, RoutedEventArgs e) => CancelAiRequest();
}
