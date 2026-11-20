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
}
