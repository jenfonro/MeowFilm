using System.Collections.ObjectModel;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Capabilities;

public sealed class NovelSourceFieldItem
{
    internal NovelSourceField Field { get; }
    public string Label => Field.Label;
    public string AutomationId => "novel.source.field." + Field.Path;
    public bool IsBool => Field.Kind == NovelSourceFieldKind.Bool;
    public Visibility BoolVisibility => IsBool ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => IsBool ? Visibility.Collapsed : Visibility.Visible;
    public bool IsMultiline => Field.Rows > 1;
    public double InputHeight => Field.Rows > 1 ? Math.Max(72, Field.Rows * 23) : 25;
    public VerticalAlignment ContentAlignment => Field.Rows > 1 ? VerticalAlignment.Top : VerticalAlignment.Center;
    public ScrollBarVisibility ScrollVisibility => Field.Rows > 1 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
    public bool? BoolValue { get; }
    public string TextValue { get; }

    internal NovelSourceFieldItem(NovelSourceField field, NovelSourceDraft draft)
    {
        Field = field;
        string raw = draft.Text(field);
        BoolValue = raw == "true";
        TextValue = raw;
    }
}

public sealed class NovelSourceTabItem
{
    public static readonly string[] ContentTypeLabels = ["文本", "音频", "图片", "文件"];

    public string Title { get; }
    public Visibility BasicVisibility { get; }
    public string[] ContentTypes => ContentTypeLabels;
    public int ContentTypeIndex { get; }
    internal int TrackedContentTypeIndex { get; set; }
    public bool? Enabled { get; }
    public bool? EnabledExplore { get; }
    public bool? EnabledCookieJar { get; }
    public NovelSourceFieldItem[] Fields { get; }

    internal NovelSourceTabItem(NovelSourceTab tab, NovelSourceDraft draft)
    {
        Title = tab.Title;
        bool isBasic = tab.Title == "基本";
        BasicVisibility = isBasic ? Visibility.Visible : Visibility.Collapsed;
        ContentTypeIndex = isBasic && int.TryParse(draft.Text(new("bookSourceType", "内容类型")), out int current) && current is >= 0 and < 4 ? current : 0;
        TrackedContentTypeIndex = ContentTypeIndex;
        Enabled = isBasic && draft.Bool("enabled");
        EnabledExplore = isBasic && draft.Bool("enabledExplore");
        EnabledCookieJar = isBasic && draft.Bool("enabledCookieJar");
        Fields = tab.Fields.Select(f => new NovelSourceFieldItem(f, draft)).ToArray();
    }
}

public sealed partial class NovelSourceEditor : UserControl
{
    internal static readonly string[] Protocols = ["legado", "qysg"];
    private readonly ObservableCollection<NovelSourceTabItem> tabItems = [];
    private bool binding;

    internal NovelSourceDraft Draft { get; private set; }

    public NovelSourceEditor()
    {
        Draft = new("legado", null);
        InitializeComponent();
        Tabs.TabItemsSource = tabItems;
    }

    internal void Load(string protocol, string? definition)
    {
        Draft = new(protocol, definition);
        Render();
    }

    internal void ChangeProtocol(string protocol)
    {
        Draft = new(protocol);
        Render();
    }

    private void Render()
    {
        binding = true;
        try
        {
            tabItems.Clear();
            var allTabs = Draft.Tabs.Concat(Draft.ExtensionFields.Length == 0
                ? []
                : new[] { new NovelSourceTab("扩展字段", Draft.ExtensionFields) });
            foreach (var tab in allTabs)
                tabItems.Add(new NovelSourceTabItem(tab, Draft));
            if (tabItems.Count > 0) Tabs.SelectedIndex = 0;
        }
        finally
        {
            binding = false;
        }
    }

    private void ContentType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (binding || sender is not ComboBox { DataContext: NovelSourceTabItem { BasicVisibility: Visibility.Visible } tab, SelectedIndex: >= 0 and < 4 } combo) return;
        if (combo.SelectedIndex == tab.TrackedContentTypeIndex) return;
        tab.TrackedContentTypeIndex = combo.SelectedIndex;
        Draft.Set(new("bookSourceType", "内容类型", Kind: NovelSourceFieldKind.Number), combo.SelectedIndex.ToString());
    }

    private void BasicCheck_Click(object sender, RoutedEventArgs e)
    {
        if (binding || sender is not CheckBox { Tag: string key } check) return;
        Draft.SetBool(key, check.IsChecked == true);
    }

    private void BoolField_Click(object sender, RoutedEventArgs e)
    {
        if (binding || sender is not CheckBox { DataContext: NovelSourceFieldItem item } check) return;
        Draft.Set(item.Field, check.IsChecked == true ? "true" : "false");
    }

    private void TextField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (binding || sender is not TextBox { DataContext: NovelSourceFieldItem item } box) return;
        Draft.Set(item.Field, box.Text);
    }

    private void TextField_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { AcceptsReturn: true } input || e.Key != VirtualKey.Tab || DesktopVisual.Modifiers != VirtualKeyModifiers.None)
            return;
        int start = input.SelectionStart;
        input.SelectedText = "\t";
        input.SelectionStart = start + 1;
        input.SelectionLength = 0;
        e.Handled = true;
    }
}
