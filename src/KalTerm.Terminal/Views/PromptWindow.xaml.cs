using System.Windows;

namespace KalTerm.Terminal.Views;

internal sealed partial class PromptWindow : Window
{
    public PromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }
}
