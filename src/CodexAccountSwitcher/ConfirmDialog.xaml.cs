using System.Windows;
using System.Windows.Input;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        SourceInitialized += (_, _) => WindowBackdrop.Apply(this);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
