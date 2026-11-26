using Argus.Core.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Argus.App.ViewModels;

public sealed partial class NavigationItemViewModel : ObservableObject
{
    public NavigationItemViewModel(
        ArgusSectionId sectionId,
        string title,
        string iconResourceKey,
        string? badgeText = null)
    {
        SectionId = sectionId;
        Title = title;
        IconResourceKey = iconResourceKey;
        BadgeText = badgeText;
    }

    public ArgusSectionId SectionId { get; }
    public string Title { get; }
    public string IconResourceKey { get; }
    public string? BadgeText { get; }
    public bool HasBadge => !string.IsNullOrWhiteSpace(BadgeText);

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isVisible = true;
}

public sealed class NavigationGroupViewModel
{
    public NavigationGroupViewModel(
        string id,
        string title,
        IReadOnlyList<NavigationItemViewModel> items)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Items = items ?? throw new ArgumentNullException(nameof(items));
    }

    public string Id { get; }
    public string Title { get; }
    public IReadOnlyList<NavigationItemViewModel> Items { get; }
}
