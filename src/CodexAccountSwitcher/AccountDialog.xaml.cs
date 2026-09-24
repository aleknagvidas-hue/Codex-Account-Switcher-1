using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher;

public partial class AccountDialog : Window
{
    public AccountDialog(
        string title,
        string initialName = "",
        string initialColor = "#7C8CFF",
        string? initialPurchaseUrl = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        AliasBox.Text = initialName;
        PurchaseLinkBox.Text = initialPurchaseUrl ?? string.Empty;
        SelectedColor = initialColor;
        SourceInitialized += (_, _) => WindowBackdrop.Apply(this);
        Loaded += (_, _) =>
        {
            AliasBox.Focus();
            AliasBox.SelectAll();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    public string Alias => AliasBox.Text.Trim();
    public string SelectedColor { get; private set; }
    public string? PurchaseUrl { get; private set; }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            SelectedColor = color;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Alias.Length is < 1 or > 40)
        {
            AliasBox.Focus();
            return;
        }

        try
        {
            PurchaseUrl = PurchaseLinkService.Normalize(PurchaseLinkBox.Text);
            LinkErrorText.Visibility = Visibility.Collapsed;
        }
        catch (ArgumentException)
        {
            LinkErrorText.Visibility = Visibility.Visible;
            PurchaseLinkBox.Focus();
            PurchaseLinkBox.SelectAll();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
