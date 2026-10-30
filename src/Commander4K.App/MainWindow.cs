using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;

namespace Commander4K.App;

// Placeholder shell; the dual-pane layout arrives in Step 4.
public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "Commander4K";
        Width = 1280;
        Height = 800;
        Content = new TextBlock()
            .Text("Commander4K")
            .HorizontalAlignment(HorizontalAlignment.Center)
            .VerticalAlignment(VerticalAlignment.Center);
    }
}
