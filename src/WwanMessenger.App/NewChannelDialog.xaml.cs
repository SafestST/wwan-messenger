using System.Windows;
using System.Windows.Input;

namespace WwanMessenger.App;

public partial class NewChannelDialog : Window
{
    public NewChannelDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NumberInput.Focus();
    }

    private void OnOpen(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }
}
