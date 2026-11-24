using Argus.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Argus.App.Views;

public sealed partial class ProcessesView : UserControl
{
    public ProcessesView()
    {
        InitializeComponent();
    }

    private void OnGroupHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is not Control { DataContext: ProcessGroupViewModel group })
        {
            return;
        }

        for (Visual? current = e.Source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Button)
            {
                return;
            }

            if (ReferenceEquals(current, sender))
            {
                break;
            }
        }

        group.ToggleExpandedCommand.Execute(null);
    }
}
