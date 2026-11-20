using Argus.Core.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Argus.App.ViewModels;

public abstract class SectionViewModelBase(ArgusSectionId sectionId, string title, string subtitle) : ObservableObject
{
    public ArgusSectionId SectionId { get; } = sectionId;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
}
