using System.Windows;

namespace KalTerm.Terminal.Views;

internal sealed partial class OrderTicketWindow : Window
{
    public OrderTicketWindow()
    {
        InitializeComponent();

        // The production confirmation is typed, not pasted: a pasted order phrase proves nothing about having read the order.
        DataObject.AddPastingHandler(TypedBox, (_, e) => e.CancelCommand());
    }
}
