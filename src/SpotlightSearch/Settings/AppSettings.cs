using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotlightSearch.Settings;

/// <summary>
/// Everything the settings dialog can change. Kept flat and JSON-friendly so the
/// file stays hand-editable.
/// </summary>
public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Alt+Space";

    /// <summary>Open straight into the AI panel instead of search.</summary>
    public bool OpenInAiMode { get; set; }

    public int ResultCount { get; set; } = 10;

    public bool HistoryEnabled { get; set; } = true;

    public bool FileSearchEnabled { get; set; } = true;

    public int FileSearchBudgetMs { get; set; } = 1500;

    /// <summary>OpenAI / Gemini / Claude / DeepSeek / Custom.</summary>
    public string AiProvider { get; set; } = "DeepSeek";

    public string AiModel { get; set; } = "deepseek-chat";

    /// <summary>Only used when AiProvider is Custom (OpenAI-compatible endpoint).</summary>
    public string AiBaseUrl { get; set; } = string.Empty;

    /// <summary>DPAPI-protected base64. Never written in clear text.</summary>
    public string AiApiKeyProtected { get; set; } = string.Empty;

    /// <summary>Characters revealed per second while the answer streams in.</summary>
    public double AiRevealCharsPerSecond { get; set; } = 260;

    public bool AutoStart { get; set; }

    [JsonIgnore]
    public string AiApiKey
    {
        get => ProtectedString.Unprotect(AiApiKeyProtected);
        set => AiApiKeyProtected = ProtectedString.Protect(value);
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "Alt+Space";
        ResultCount = Math.Clamp(ResultCount, 4, 20);
        FileSearchBudgetMs = Math.Clamp(FileSearchBudgetMs, 200, 5000);
        AiRevealCharsPerSecond = Math.Clamp(AiRevealCharsPerSecond, 40, 2000);
        AiProvider = string.IsNullOrWhiteSpace(AiProvider) ? "DeepSeek" : AiProvider.Trim();
        AiModel = AiModel?.Trim() ?? string.Empty;
        AiBaseUrl = AiBaseUrl?.Trim() ?? string.Empty;
    }
}

/// <summary>settings.json under %APPDATA%\SpotlightSearch.</summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly object _gate = new();
    private readonly string _path;
    private AppSettings _current;

    public SettingsStore()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotlightSearch");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        _current = Load();
    }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler? Changed;

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        lock (_gate)
        {
            _current = settings;
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception)
            {
                // A read-only profile directory should not break the app.
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
                if (loaded != null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt settings fall back to defaults instead of blocking startup.
        }

        var defaults = new AppSettings();
        defaults.Normalize();
        return defaults;
    }
}
