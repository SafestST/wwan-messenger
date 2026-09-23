using System.Windows;
using System.Windows.Input;

namespace WwanMessenger.App;

public partial class RenameContactDialog : Window
{
    public RenameContactDialog() : this(null) { }

    /// <param name="currentName">Current display name (not the number
    /// fallback) — prefilled and selected so typing replaces it.</param>
    public RenameContactDialog(string? currentName)
    {
        InitializeComponent();
        NameInput.Text = currentName ?? "";
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }
}
