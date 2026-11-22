using Argus.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Argus.App.Views;

public sealed partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is DevicesViewModel vm)
        {
            vm.CopyToClipboardHandler = text =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                {
                    _ = clipboard.SetTextAsync(text);
                }
            };
        }
    }
}
