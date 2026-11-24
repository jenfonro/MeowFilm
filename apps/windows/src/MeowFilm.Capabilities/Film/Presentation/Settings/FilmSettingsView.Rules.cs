using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Capabilities.Film.Rules;

namespace MeowFilm.Capabilities;

public partial class FilmSettingsView
{
    private CancellationTokenSource? ruleOperation;
    private FilmSettingsRuleEditor[]? ruleEditors;
    private bool rulesLoaded, rulesBusy, rulesFailed, replacingRules, rulesReloadPending;
    public bool RulesLoaded => rulesLoaded;
    public bool RulesBusy => rulesBusy;
    public bool RulesIdle => !rulesBusy;
    public bool RulesFailed => rulesFailed;
    public bool CanEditRules => rulesLoaded && !rulesBusy;
    private FilmSettingsRuleEditor[] RuleEditors => ruleEditors ??= [EpisodeCleanEditor, EpisodeEditor, MovieEditor, SearchEditor];

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
        if (Active && ticket != activation) await ActivateCurrentGuardedAsync();
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
        var list = values as IList<FilmMatchRule> ?? values.ToList();
        for (int index = 0; index < list.Count; index++)
        {
            var value = list[index];
            if (index < editor.Rows.Count)
                editor.Rows[index].Update(index + 1, value.Pattern, value.Replace, value.Flags);
            else
                editor.Rows.Add(new(editor.Title, index + 1, value.Pattern, value.Replace, value.Flags, editor.HasReplacement));
        }
        while (editor.Rows.Count > list.Count)
            editor.Rows.RemoveAt(editor.Rows.Count - 1);
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
        string groupTag = editor.Tag as string ?? "";
        bool hasReplacement = editor.HasReplacement;
        int index = row is null ? -1 : editor.Rows.IndexOf(row);
        return RunRulesAsync(async token =>
        {
            var next = FilmRuleEditorLogic.ApplyGroupEdit(draft, groupTag, action, index, hasReplacement, pattern, replace, rules);
            var saved = await rules.SaveAsync(next, token);
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
        OpenPopup(anchor, "恢复默认规则", $"恢复“{FilmRuleEditorLogic.SectionTitle(target)}”的默认规则？", "恢复", () =>
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

    public ObservableCollection<FilmRuleResultLine> EpisodeResultLines { get; } = [];
    public ObservableCollection<FilmRuleResultLine> MovieResultLines { get; } = [];
    public ObservableCollection<FilmRuleResultLine> SearchResultLines { get; } = [];

    private void ShowRuleResult(FilmRuleResult result)
    {
        var lines = result.Section switch { FilmRuleSection.Episode => EpisodeResultLines, FilmRuleSection.Movie => MovieResultLines, _ => SearchResultLines };
        lines.Clear();
        foreach (var line in FilmRuleEditorLogic.BuildResultLines(result)) lines.Add(line);
        var editor = result.Section switch { FilmRuleSection.Episode => EpisodeCleanEditor, FilmRuleSection.Search => SearchEditor, _ => null };
        if (editor is not null)
            for (int i = 0; i < editor.Rows.Count; i++)
                editor.Rows[i].Preview = i < result.Previews.Count ? $"净化后：{result.Previews[i]}" : "";
    }

    private void InvalidateRuleResults()
    {
        if (!initialized || replacingRules) return;
        if (EpisodeResultLines.Count > 0) EpisodeResultLines.Clear();
        if (MovieResultLines.Count > 0) MovieResultLines.Clear();
        if (SearchResultLines.Count > 0) SearchResultLines.Clear();
        foreach (var editor in RuleEditors)
            foreach (var row in editor.Rows) row.Preview = "";
    }
    private void RuleInput_Changed(object sender, TextChangedEventArgs e) => InvalidateRuleResults();
    private async void RulesRetry_Click(object sender, RoutedEventArgs e) => await LoadRulesAsync();
    private void RulesCancel_Click(object sender, RoutedEventArgs e) => ruleOperation?.Cancel();
}
