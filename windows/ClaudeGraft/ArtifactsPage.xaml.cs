using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

public sealed partial class ArtifactsPage : Page
{
    private readonly ArtifactCenter _center = ArtifactCenter.Shared;

    public ArtifactsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _center.Changed += Render;
            Render();
            _center.Refresh();
        };
        Unloaded += (_, _) => _center.Changed -= Render;
    }

    /// Shown again from the sidebar: asked afresh, as on the Mac.
    public void OnShown() => _center.Refresh();

    private void AddAccount_Click(object sender, RoutedEventArgs e) => _center.SignInNewAccount();
    private void Refresh_Click(object sender, RoutedEventArgs e) => _center.Refresh();

    private void Render()
    {
        AccountList.Items.Clear();
        foreach (var account in _center.Accounts)
        {
            var state = _center.StateOf(account.Id);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = _center.NameOf(account.Id), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock
            {
                Text = state switch
                {
                    ArtifactCenter.AccountState.Loading => "Loading…",
                    ArtifactCenter.AccountState.SignedOut => "Signed out",
                    ArtifactCenter.AccountState.Failed => "claude.ai did not answer",
                    _ => "",
                },
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            var more = new DropDownButton { Content = "Account" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, $"Account {_center.NameOf(account.Id)}");
            var menu = new MenuFlyout();
            var again = new MenuFlyoutItem { Text = "Sign In Again…" };
            var id = account.Id;
            again.Click += (_, _) => _center.SignInAgain(id);
            var signOut = new MenuFlyoutItem { Text = "Sign Out" };
            signOut.Click += (_, _) => _center.SignOut(id);
            menu.Items.Add(again);
            menu.Items.Add(signOut);
            more.Flyout = menu;
            row.Children.Add(more);
            AccountList.Items.Add(row);
        }
        StatusLine.Text = _center.Status ?? "";
        StatusLine.Visibility = _center.Status is null ? Visibility.Collapsed : Visibility.Visible;

        ArtifactList.Items.Clear();
        foreach (var artifact in _center.Artifacts)
        {
            var row = new StackPanel { Spacing = 2, Padding = new Thickness(0, 6, 0, 6) };
            row.Children.Add(new TextBlock { Text = artifact.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            row.Children.Add(new TextBlock
            {
                Text = $"{_center.NameOf(artifact.Account)}  ·  {artifact.Access.Label()}" + (artifact.IsOwn ? "" : "  ·  Shared with you"),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            ArtifactList.Items.Add(row);
        }
    }
}
