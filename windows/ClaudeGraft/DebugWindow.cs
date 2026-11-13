using ClaudeGraft.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeGraft;

/// <summary>
/// Puts Graft into states that are hard to reach for real, so each can be looked
/// at. Everything is staged in memory (<see cref="Staging"/>) and gone at quit or
/// Reset; nothing staged is ever installed, restarted or written.
/// </summary>
internal sealed class DebugWindow : Window
{
    private static DebugWindow? _open;

    public static void Show()
    {
        if (_open is null)
        {
            _open = new DebugWindow();
            _open.Closed += (_, _) => _open = null;
        }
        _open.Activate();
    }

    private readonly StackPanel _accounts = new() { Spacing = 8 };
    private bool _drawing;

    private DebugWindow()
    {
        Title = "Claude Graft Debug";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 720));

        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "Nothing here is saved, installed or sent. Quitting Graft or Reset puts it all back.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
        });

        Heading(panel, "Updates");
        panel.Children.Add(Choice("Claude Desktop", "debug.claudeUpdate",
            ["As it is", "Up to date", "Checking", "Update available", "Installing", "Needs restart", "Restarting", "Check failed", "Microsoft Store"],
            index =>
            {
                if (index == 0) { ClaudeUpdateCenter.Unstage(); return; }
                ClaudeUpdateCenter.Stage(index switch
                {
                    1 => ClaudeUpdateCenter.Phase.UpToDate,
                    2 => ClaudeUpdateCenter.Phase.Checking,
                    3 => ClaudeUpdateCenter.Phase.Available,
                    4 => ClaudeUpdateCenter.Phase.Installing,
                    5 => ClaudeUpdateCenter.Phase.NeedsRestart,
                    6 => ClaudeUpdateCenter.Phase.Restarting,
                    7 => ClaudeUpdateCenter.Phase.Failed,
                    _ => ClaudeUpdateCenter.Phase.Store,
                });
            }));
        panel.Children.Add(Choice("Claude Graft", "debug.graftUpdate", ["As it is", "Update available"],
            index => App.Instance.StageUpdate(index == 1)));

        Heading(panel, "Setup and links");
        panel.Children.Add(Choice("Setup", "debug.setup", ["As it is", "Ready", "Claude not installed", "Nobody signed in"],
            index => Staging.Setup = index switch
            {
                1 => SetupState.Ready,
                2 => SetupState.NotInstalled,
                3 => SetupState.NotSignedIn,
                _ => null,
            }));
        panel.Children.Add(Choice("Claude links handled by", "debug.links", ["As it is", "Graft", "Another app", "Unknown"],
            index => Staging.LinkHandler = index switch
            {
                1 => ClaudeLinkHandler.Graft,
                2 => ClaudeLinkHandler.Other,
                3 => ClaudeLinkHandler.Unknown,
                _ => null,
            }));

        Heading(panel, "Errors");
        panel.Children.Add(Choice("Every state pass", "debug.stateLock", ["Runs", "Finds Graft busy", "Finds mirrored-chats.json unreadable"],
            index => Staging.StateFailure = index switch
            {
                1 => () => new StateBusyException(),
                2 => () => new UnreadableStateException(Path.Combine(GraftPaths.OwnData, "mirrored-chats.json"), null),
                _ => null,
            }));

        Heading(panel, "Accounts");
        panel.Children.Add(_accounts);
        DrawAccounts();

        var reset = new Button { Content = "Reset Everything", Margin = new Thickness(0, 16, 0, 0) };
        Identify(reset, "Reset Everything", "debug.reset");
        reset.Click += (_, _) => Reset(panel);
        panel.Children.Add(reset);

        Content = new ScrollViewer
        {
            Content = panel,
            Background = Themed.Brush("SolidBackgroundFillColorBaseBrush"),
        };
    }

    private static readonly string[] AccountStates =
    [
        "As it is", "Signed out", "Open", "Usage low", "Usage high", "At the five-hour limit",
        "No five-hour limit", "Rate limited", "Login refused", "Unreachable", "No usage yet",
    ];

    private void DrawAccounts()
    {
        _accounts.Children.Clear();
        var profiles = App.Store.Shortcuts.Select(s => (s.Name, s.ProfileDir)).Prepend(("Claude", GraftPaths.DefaultProfile));
        foreach (var (name, profile) in profiles)
            _accounts.Children.Add(Choice(name, "debug.account." + Path.GetFileName(profile), AccountStates,
                index => Staging.Stage(profile, AccountFor(index))));
    }

    private static AccountStage? AccountFor(int index)
    {
        var now = DateTimeOffset.UtcNow;
        UsageEntry Figure(int? fiveHour, int week, RefusalKind? refused = null) => new()
        {
            Usage = new Usage
            {
                FiveHour = fiveHour, Week = week, Sampled = now,
                FiveHourReset = fiveHour is null ? null : now.AddHours(3), WeekReset = now.AddDays(4),
            },
            IsLive = refused is null, Plan = "Max", UpdatedAt = refused is null ? now : now.AddMinutes(-20),
            Refusal = refused is { } kind ? new UsageRefusal { Kind = kind, RetryAt = now.AddMinutes(5) } : null,
        };
        return index switch
        {
            1 => new AccountStage { SignedIn = false },
            2 => new AccountStage { Running = true },
            3 => new AccountStage { SignedIn = true, Usage = Figure(12, 20) },
            4 => new AccountStage { SignedIn = true, Usage = Figure(86, 91) },
            5 => new AccountStage { SignedIn = true, Usage = Figure(100, 64) },
            6 => new AccountStage { SignedIn = true, Usage = Figure(null, 40) },
            7 => new AccountStage { SignedIn = true, Usage = Figure(55, 30, RefusalKind.RateLimited) },
            8 => new AccountStage { SignedIn = true, Usage = Figure(55, 30, RefusalKind.LoginRefused) },
            9 => new AccountStage { SignedIn = true, Usage = Figure(55, 30, RefusalKind.Unreachable) },
            10 => new AccountStage { SignedIn = true, Usage = new UsageEntry { IsLive = false } },
            _ => null,
        };
    }

    private void Reset(StackPanel panel)
    {
        _drawing = true;
        foreach (var box in Boxes(panel)) box.SelectedIndex = 0;
        _drawing = false;
        ClaudeUpdateCenter.Unstage();
        if (App.Instance.UpdateStaged) App.Instance.StageUpdate(false);
        Staging.Clear();
    }

    private static IEnumerable<ComboBox> Boxes(DependencyObject root)
    {
        if (root is ComboBox box) yield return box;
        if (root is not Panel panel) yield break;
        foreach (var child in panel.Children)
            foreach (var found in Boxes(child)) yield return found;
    }

    private static void Heading(Panel panel, string text) => panel.Children.Add(new TextBlock
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, 16, 0, 0),
    });

    private Grid Choice(string label, string id, string[] options, Action<int> chosen)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(name, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var box = new ComboBox { ItemsSource = options, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(box, 1);
        Identify(box, label, id);
        box.SelectionChanged += (_, _) =>
        {
            if (_drawing || box.SelectedIndex < 0) return;
            chosen(box.SelectedIndex);
            Staging.Announce();
        };
        row.Children.Add(name);
        row.Children.Add(box);
        return row;
    }

    private static void Identify(FrameworkElement control, string name, string id)
    {
        AutomationProperties.SetName(control, name);
        AutomationProperties.SetAutomationId(control, id);
    }
}
