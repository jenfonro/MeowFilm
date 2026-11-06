using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MeowFilm.Capabilities.Film.Rules;

namespace MeowFilm.Capabilities;

public partial class FilmSettingsView
{
    private CancellationTokenSource? ruleOperation;
    private bool rulesLoaded, rulesBusy, rulesFailed, replacingRules, rulesReloadPending;
    public bool RulesLoaded => rulesLoaded;
    public bool RulesBusy => rulesBusy;
    public bool RulesIdle => !rulesBusy;
    public bool RulesFailed => rulesFailed;
    public bool CanEditRules => rulesLoaded && !rulesBusy;
    private FilmSettingsRuleEditor[] RuleEditors => [EpisodeCleanEditor, EpisodeEditor, MovieEditor, SearchEditor];

    private void RulesStateChanged()
    {
        foreach (string name in new[] { nameof(RulesLoaded), nameof(RulesBusy), nameof(RulesIdle),
            nameof(RulesFailed), nameof(CanEditRules) }) Changed(name);
    }

    private Task LoadRulesAsync() => RunRulesAsync(async token =>
    {
        await rules.InitializeAsync(token);
        var snapshot = rules.GetSavedSnapshot();
        return () => { ReplaceRules(snapshot); rulesLoaded = true; rulesReloadPending = false; };
    });

    private async Task RunRulesAsync(Func<CancellationToken, Task<Action>> work, string? success = null,
        bool changesSavedRules = false)
    {
        if (!Active || RulesBusy) return;
        using var operation = new CancellationTokenSource();
        ruleOperation = operation;
        long ticket = activation;
        rulesBusy = true; rulesFailed = false;
        Notices.Message = "";
        RulesStateChanged();
        try
        {
            var publish = await work(operation.Token);
            if ((!operation.IsCancellationRequested || changesSavedRules) && Active && ticket == activation)
            {
                publish();
                if (success is not null) Notify(success);
            }
            else if (changesSavedRules) rulesReloadPending = true;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (Active && ticket == activation) rulesFailed = !rulesLoaded;
        }
        catch (Exception error)
        {
            if (Active && ticket == activation)
            {
                rulesFailed = !rulesLoaded;
                Notify("规则操作失败：" + error.Message);
            }
        }
        finally
        {
            ruleOperation = null; rulesBusy = false;
            RulesStateChanged();
        }
        if (Active && ticket != activation) await ActivateCurrentAsync();
    }

    private FilmRuleSet ReadDraft() => new()
    {
        EpisodeCleanRegexRules = EpisodeCleanEditor.Rows.Select(row => row.Pattern).ToArray(),
        EpisodeRules = EpisodeEditor.Rows.Select(Match).ToArray(),
        MovieRules = MovieEditor.Rows.Select(Match).ToArray(),
        AggregateRegexRules = SearchEditor.Rows.Select(row => row.Pattern).ToArray()
    };
    private static FilmMatchRule Match(FilmSettingsRuleRow row) => new(row.Pattern, row.Replace, row.Flags);

    private void ReplaceRules(FilmRuleSet snapshot)
    {
        replacingRules = true;
        try
        {
            SetRows(EpisodeCleanEditor, snapshot.EpisodeCleanRegexRules.Select(pattern => new FilmMatchRule(pattern)));
            SetRows(EpisodeEditor, snapshot.EpisodeRules);
            SetRows(MovieEditor, snapshot.MovieRules);
            SetRows(SearchEditor, snapshot.AggregateRegexRules.Select(pattern => new FilmMatchRule(pattern)));
        }
        finally { replacingRules = false; }
        InvalidateRuleResults();
    }

    private static void SetRows(FilmSettingsRuleEditor editor, IEnumerable<FilmMatchRule> values)
    {
        while (editor.Rows.Count > 0) editor.Rows.RemoveAt(editor.Rows.Count - 1);
        foreach (var value in values)
            editor.Rows.Add(new(editor.Title, editor.Rows.Count + 1, value.Pattern, value.Replace, value.Flags));
    }

    private async void Rule_ActionRequested(object? sender, FilmSettingsRuleActionEventArgs e)
    {
        if (!Active || !CanEditRules || sender is not FilmSettingsRuleEditor editor) return;
        if (e.Action != FilmSettingsRuleAction.Add && (e.Row is null || !editor.Rows.Contains(e.Row))) return;
        if (e.Action == FilmSettingsRuleAction.Delete)
        {
            OpenPopup(e.Anchor, "删除规则", $"删除“{editor.Title}”的这条规则？", "删除", () =>
            {
                if (!editor.Rows.Contains(e.Row!) || !CanEditRules) return Task.CompletedTask;
                return SaveRuleChangeAsync(editor, e.Action, e.Row);
            });
        }
        else await SaveRuleChangeAsync(editor, e.Action, e.Row);
    }

    private Task SaveRuleChangeAsync(FilmSettingsRuleEditor editor, FilmSettingsRuleAction action, FilmSettingsRuleRow? row)
    {
        var draft = ReadDraft();
        string pattern = editor.NewPatternText, replace = editor.NewReplaceText;
        int index = row is null ? -1 : editor.Rows.IndexOf(row);
        return RunRulesAsync(async token =>
        {
            FilmMatchRule? addedMatch = null;
            string? addedClean = null;
            if (action == FilmSettingsRuleAction.Add)
            {
                if (editor.HasReplacement)
                    addedMatch = await rules.NormalizeNewMatchRuleAsync(pattern, replace, token)
                        ?? throw new ArgumentException("请输入正则");
                else
                {
                    addedClean = await rules.NormalizeNewCleanRuleAsync(pattern, token);
                    if (addedClean.Length == 0) throw new ArgumentException("请输入正则");
                }
            }
            static T[] Edit<T>(IReadOnlyList<T> values, FilmSettingsRuleAction change, int at, T? added)
            {
                var copy = values.ToList();
                if (change == FilmSettingsRuleAction.Delete) copy.RemoveAt(at);
                else if (change == FilmSettingsRuleAction.Add) copy.Add(added!);
                return copy.ToArray();
            }
            draft = (editor.Tag as string) switch
            {
                "episodeClean" => draft with { EpisodeCleanRegexRules = Edit(draft.EpisodeCleanRegexRules, action, index, addedClean) },
                "episode" => draft with { EpisodeRules = Edit(draft.EpisodeRules, action, index, addedMatch) },
                "movie" => draft with { MovieRules = Edit(draft.MovieRules, action, index, addedMatch) },
                "search" => draft with { AggregateRegexRules = Edit(draft.AggregateRegexRules, action, index, addedClean) },
                _ => throw new InvalidOperationException("未知规则组")
            };
            var saved = await rules.SaveAsync(draft, token);
            return () =>
            {
                ReplaceRules(saved);
                if (action == FilmSettingsRuleAction.Add) editor.ClearNewRule();
            };
        }, "规则已保存", changesSavedRules: true);
    }

    private void RestoreRules_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditRules || sender is not Button { Tag: string key } anchor ||
            !Enum.TryParse<FilmRuleSection>(key, out var target)) return;
        string title = target switch { FilmRuleSection.Episode => "列表净化", FilmRuleSection.Movie => "电影匹配", _ => "搜索列表净化" };
        OpenPopup(anchor, "恢复默认规则", $"恢复“{title}”的默认规则？", "恢复", () =>
        {
            var draft = ReadDraft();
            return RunRulesAsync(async token =>
            {
                var saved = await rules.RestoreDefaultsAsync(target, draft, token);
                return () => ReplaceRules(saved);
            }, "默认规则已恢复", changesSavedRules: true);
        });
    }

    private async void TestRules_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditRules || sender is not Button { Tag: string key } ||
            !Enum.TryParse<FilmRuleSection>(key, out var target)) return;
        var request = target switch
        {
            FilmRuleSection.Episode => new FilmRuleRequest(target, EpisodeInput.Text),
            FilmRuleSection.Movie => new FilmRuleRequest(target, MovieInput.Text),
            _ => new FilmRuleRequest(target, SearchInput.Text, SearchQuery.Text)
        };
        var draft = ReadDraft();
        await RunRulesAsync(async token =>
        {
            var result = await rules.ProcessAsync(request, draft, token);
            return () => ShowRuleResult(result);
        });
    }

    private void ShowRuleResult(FilmRuleResult result)
    {
        var panel = result.Section switch { FilmRuleSection.Episode => EpisodeResults, FilmRuleSection.Movie => MovieResults, _ => SearchResults };
        panel.Children.Clear();
        void Add(string name, string value)
        {
            var text = new TextBlock { Text = name + "：" + value, TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 0) };
            text.SetResourceReference(StyleProperty, "Settings.Text");
            AutomationProperties.SetName(text, name);
            panel.Children.Add(text);
        }
        Add("结果", result.Text);
        if (result.InputText.Length != 0) Add(result.Section == FilmRuleSection.Movie ? "匹配文本" : "净化后", result.ProcessedText);
        if (result.MatchedRuleIndex is { } index) Add("命中规则", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (result.RewrittenText is { } rewrite) Add("改写结果", rewrite);
        if (result.Section == FilmRuleSection.Search && result.InputText.Length != 0)
        {
            Add("聚合 Key", result.GroupKey);
            if (result.TrailingDigits.Length != 0) Add("搜索尾号保护", result.TrailingDigits);
        }
        foreach (var error in result.Errors) Add($"规则 {error.RuleIndex}", error.Text);
        var editor = result.Section switch { FilmRuleSection.Episode => EpisodeCleanEditor, FilmRuleSection.Search => SearchEditor, _ => null };
        if (editor is not null)
            for (int i = 0; i < editor.Rows.Count; i++)
                editor.Rows[i].Preview = i < result.Previews.Count ? $"净化后：{result.Previews[i]}" : "";
    }

    private void InvalidateRuleResults()
    {
        if (!initialized || replacingRules) return;
        EpisodeResults.Children.Clear(); MovieResults.Children.Clear(); SearchResults.Children.Clear();
        foreach (var editor in RuleEditors)
            foreach (var row in editor.Rows) row.Preview = "";
    }
    private void RuleInput_Changed(object sender, TextChangedEventArgs e) => InvalidateRuleResults();
    private async void RulesRetry_Click(object sender, RoutedEventArgs e) => await LoadRulesAsync();
    private void RulesCancel_Click(object sender, RoutedEventArgs e) => ruleOperation?.Cancel();
}
