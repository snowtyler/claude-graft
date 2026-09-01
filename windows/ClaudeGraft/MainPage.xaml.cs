using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using System;

namespace ClaudeGraft;

/// <summary>
/// The main content page displayed inside the application window.
/// </summary>
public sealed partial class MainPage : Page
{
    private int _clickCount = 0;

    public MainPage()
    {
        InitializeComponent();
        InitializePackageStatus();
        this.Loaded += MainPage_Loaded;
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeThemeToggle();
    }

    private void InitializePackageStatus()
    {
        if (IsPackaged())
        {
            PackageStatusText.Text = "Status: Running Packaged (MSIX)";
            PackageStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Green);
        }
        else
        {
            PackageStatusText.Text = "Status: Running Unpackaged";
            PackageStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange);
        }
    }

    private bool IsPackaged()
    {
        try
        {
            var package = Windows.ApplicationModel.Package.Current;
            return package != null && package.Id != null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void InitializeThemeToggle()
    {
        if (ThemeToggle == null) return;

        var element = App.MainWindow?.Content as FrameworkElement;
        if (element != null)
        {
            bool isDark = false;
            if (element.RequestedTheme == ElementTheme.Default)
            {
                isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
            else
            {
                isDark = element.RequestedTheme == ElementTheme.Dark;
            }

            ThemeToggle.IsOn = isDark;
        }
    }

    private void ThemeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ThemeToggle == null) return;
        
        var element = App.MainWindow?.Content as FrameworkElement;
        if (element != null)
        {
            var isDark = ThemeToggle.IsOn;
            element.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;

            if (App.MainWindow?.AppWindow != null && Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
            {
                App.MainWindow.AppWindow.TitleBar.PreferredTheme = isDark ? Microsoft.UI.Windowing.TitleBarTheme.Dark : Microsoft.UI.Windowing.TitleBarTheme.Light;
            }
        }
    }

    private void ClickMeButton_Click(object sender, RoutedEventArgs e)
    {
        _clickCount++;
        CounterText.Text = $"Clicks: {_clickCount}";
    }

    private void BackdropComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackdropComboBox == null || App.MainWindow == null) return;

        var selectedIndex = BackdropComboBox.SelectedIndex;
        SystemBackdrop? backdrop = selectedIndex switch
        {
            0 => new MicaBackdrop { Kind = MicaKind.Base },
            1 => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            2 => new DesktopAcrylicBackdrop(),
            _ => null
        };

        App.MainWindow.SystemBackdrop = backdrop!;
    }
}
