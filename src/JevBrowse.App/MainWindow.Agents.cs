using JevBrowse.AgentGateway;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace JevBrowse.App;

/// <summary>
/// Agent activity (P3): who is acting on the browser right now, what they may do and what they just did, including what was
/// refused, with a Stop that works in one click. Stopping is the safe direction, so it never asks first; setting up access
/// (a decision about what agents may ever do) stays the Agent Gateway dialog.
/// </summary>
public sealed partial class MainWindow
{
    private void OnAgentActivity(object s, RoutedEventArgs e) => OpenPanel("agents", BuildAgentActivity, MoreButton, live: false);

    private IReadOnlyList<AgentSessionFacts> AgentFacts() =>
        (_agentHost?.Sessions ?? []).Select(ses =>
        {
            AuditEntry[] audit;
            try { audit = ses.Audit.ToArray(); }
            catch (InvalidOperationException) { audit = []; }   // the gateway appended while we copied; the next tick catches up
            return new AgentSessionFacts(ses.Id, ses.Manifest.Agent, ses.CleanedUp, ses.Closed, ses.ExpiresAt, ses.ActionsUsed, ses.Manifest.MaxActions,
                _agents!.LiveAgentPages(ses), ses.Manifest.MaxLivePages, ses.Manifest.Actions.Select(a => a.ToString()).ToList(),
                ses.Manifest.AllowDomains.ToList(), ses.Manifest.DenyDataClasses.Select(c => c.ToString()).ToList(), audit);
        }).ToList();

    private (string Title, UIElement Body)? BuildAgentActivity()
    {
        var secondary = Tokens.Brush("JevTextSecondaryBrush");
        var body = new StackPanel { Spacing = Tokens.Space(12) };
        if (_agents is null)
        {
            body.Children.Add(new TextBlock { Text = "Agent access is not available.", TextWrapping = TextWrapping.Wrap });
            return ("Agent activity", body);
        }
        if (_agentHost?.IsRunning != true)
        {
            body.Children.Add(new TextBlock { Text = "No agent can act on your browser right now. The local endpoint for agents is off.", TextWrapping = TextWrapping.Wrap });
            var setup = new Button { Content = "Set up agent access…", HorizontalAlignment = HorizontalAlignment.Stretch };
            setup.Click += (_, _) => { ClosePanel(restoreFocus: false); OnAgents(this, new RoutedEventArgs()); };
            body.Children.Add(setup);
            return ("Agent activity", body);
        }

        var cards = AgentActivity.Build(AgentFacts(), DateTimeOffset.UtcNow);
        var ids = string.Join(",", cards.Select(c => c.Id));
        var live = new Dictionary<string, (TextBlock Status, TextBlock Summary, TextBlock Recent, Button Stop)>();
        if (cards.Count == 0)
            body.Children.Add(new TextBlock { Text = "The endpoint is on, but no agent has started a session.", TextWrapping = TextWrapping.Wrap, Foreground = secondary });

        foreach (var card in cards)
        {
            var c = card;
            var status = new TextBlock { Text = c.Status, TextWrapping = TextWrapping.Wrap };
            var summary = new TextBlock { Text = c.Summary, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = secondary };
            var scope = new TextBlock { Text = c.Scope, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = secondary };
            var recent = new TextBlock { Text = RecentText(c), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
            AutomationProperties.SetName(recent, $"Recent actions by {c.Agent}");
            // Watching is the person's choice, made here. An agent's page never takes over the window by itself.
            var session0 = _agentHost.Sessions.FirstOrDefault(x => x.Id == c.Id);
            var show = new Button { Content = "Show its page", HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = c.CanStop && session0?.Current is not null };
            AutomationProperties.SetName(show, $"Show the page {c.Agent} is on, in this window");
            show.Click += async (_, _) =>
            {
                if (session0?.Current is not { } page || _kernel is null) return;
                ClosePanel(restoreFocus: false);
                if (_kernel.Tabs.All(t => t.Id != page)) return;
                await _kernel.ActivateAsync(page);
                RebuildWorkspaces();
                StatusText.Text = $"showing the page {session0.Manifest.Agent} is on; it keeps working in its own workspace";
            };
            var stop = new Button { Content = c.CanStop ? "Stop this agent" : "Stopped", IsEnabled = c.CanStop, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(stop, $"Stop {c.Agent}, session {c.Id}");
            var session = _agentHost.Sessions.FirstOrDefault(x => x.Id == c.Id);
            stop.Click += async (_, _) =>
            {
                if (session is null) return;
                stop.IsEnabled = false; stop.Content = "Stopping…";
                var released = await _agentHost!.StopAsync(session);
                StatusText.Text = released ? $"agent session {session.Id} revoked and its pages released" : $"agent session {session.Id} revoked; some pages could not be released yet";
                Tick();
            };
            live[c.Id] = (status, summary, recent, stop);
            body.Children.Add(new Border
            {
                Padding = Tokens.Inset("JevInsetBox"), CornerRadius = (Microsoft.UI.Xaml.CornerRadius)Application.Current.Resources["JevRadiusCard"],
                Background = Tokens.Brush("JevSurface2Brush"), BorderBrush = Tokens.Brush("JevBorderSubtleBrush"), BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Spacing = Tokens.Space(6),
                    Children = { new TextBlock { Text = c.Agent, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap }, status, summary, scope,
                                 new TextBlock { Text = "Recent actions", FontSize = 12, Foreground = secondary }, recent, show, stop },
                },
            });
        }
        if (cards.Any(c => c.CanStop))
        {
            var all = new Button { Content = "Stop all agents", HorizontalAlignment = HorizontalAlignment.Stretch };
            all.Click += async (_, _) => { var n = await _agentHost!.StopAllAsync(); StatusText.Text = $"revoked {n} agent session(s)"; Tick(); };
            body.Children.Add(all);
        }

        // Update the words in place. Rebuilding the panel while someone is tabbing through it would throw their focus away.
        void Tick()
        {
            if (!PanelOpen || _panelId != "agents") return;
            var now = AgentActivity.Build(AgentFacts(), DateTimeOffset.UtcNow);
            if (string.Join(",", now.Select(c => c.Id)) != ids) { RefreshPanel(force: true); return; }
            foreach (var n in now)
            {
                if (!live.TryGetValue(n.Id, out var t)) continue;
                t.Status.Text = n.Status; t.Summary.Text = n.Summary; t.Recent.Text = RecentText(n);
                if (!n.CanStop) { t.Stop.IsEnabled = false; t.Stop.Content = "Stopped"; }
            }
        }
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (_, _) => Tick();
        timer.Start();
        _panelOnClose = () => timer.Stop();
        return ("Agent activity", body);
    }

    private static string RecentText(AgentSessionCard c) => c.Recent.Count == 0 ? "nothing yet" : string.Join("\n", c.Recent);
}
