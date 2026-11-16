using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SpotlightSearch.Search;

namespace SpotlightSearch.Models;

public enum SpotlightResultKind
{
    Application,
    File,
    Folder,
    Calculation,
    Settings,
    SystemAction
}

/// <summary>
/// One row of the result list. Deliberately a record with reference equality:
/// two results that look identical are still distinct rows.
/// </summary>
public sealed record SpotlightSearchItem(
    string Id,
    SpotlightResultKind Kind,
    string Title,
    string Subtitle,
    string Target,
    string? IconPath = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Equals(SpotlightSearchItem? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    public double Score { get; init; }

    /// <summary>Title without the trailing extension, for apps and files.</summary>
    public string DisplayTitle
    {
        get
        {
            if (Kind is not (SpotlightResultKind.Application or SpotlightResultKind.File)) return Title;
            string extension = Path.GetExtension(Target);
            return extension.Length > 0 && Title.Length > extension.Length
                   && Title.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? Title[..^extension.Length]
                : Title;
        }
    }

    public string DisplaySubtitle
    {
        get
        {
            if (!Path.IsPathFullyQualified(Subtitle)) return Subtitle;
            return Kind == SpotlightResultKind.Application
                ? "应用程序"
                : Path.GetDirectoryName(Target) ?? Subtitle;
        }
    }

    public string KindLabel => Kind switch
    {
        SpotlightResultKind.Application => "应用",
        SpotlightResultKind.File => "文件",
        SpotlightResultKind.Folder => "文件夹",
        SpotlightResultKind.Calculation => "计算",
        SpotlightResultKind.Settings => "设置",
        _ => "系统"
    };

    public string Glyph => SystemCatalog.Find(Target)?.Glyph ?? Kind switch
    {
        SpotlightResultKind.Application => "\uE71D",
        SpotlightResultKind.Folder => "\uE8B7",
        SpotlightResultKind.Calculation => "\uE8EF",
        SpotlightResultKind.Settings => "\uE713",
        _ => "\uE8A5"
    };

    public bool IsRecent { get; init; }

    public bool IsShortcut => Kind == SpotlightResultKind.Application
        && string.Equals(Path.GetExtension(Target), ".lnk", StringComparison.OrdinalIgnoreCase);

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }
}
