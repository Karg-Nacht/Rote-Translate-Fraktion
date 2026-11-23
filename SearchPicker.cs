using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Rtf;

internal sealed record PickerOption(string Value, string Label);

[TemplatePart(Name = "PART_Anchor", Type = typeof(Button))]
[TemplatePart(Name = "PART_Popup", Type = typeof(Popup))]
[TemplatePart(Name = "PART_Search", Type = typeof(TextBox))]
[TemplatePart(Name = "PART_Options", Type = typeof(ListBox))]
public sealed class SearchPicker : Control
{
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(SearchPicker), new PropertyMetadata("Select an option"));

    public static readonly DependencyProperty DisplayTextProperty =
        DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(SearchPicker),
            new PropertyMetadata(null, (picker, _) => ((SearchPicker)picker).UpdateCaption()));

    private static readonly DependencyPropertyKey CaptionPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Caption), typeof(string), typeof(SearchPicker), new PropertyMetadata(null));

    public static readonly DependencyProperty CaptionProperty = CaptionPropertyKey.DependencyProperty;

    private Button? anchor;
    private Popup? popup;
    private TextBox? search;
    private ListBox? options;
    private FrameworkElement? popupContent;
    private bool suppressAnchorClick;
    private PickerOption? selectedItem;
    private IReadOnlyList<PickerOption> items = Array.Empty<PickerOption>();

    public SearchPicker() => IsVisibleChanged += (_, _) =>
    {
        if (!IsVisible)
            Close();
    };

    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public string? DisplayText { get => (string?)GetValue(DisplayTextProperty); set => SetValue(DisplayTextProperty, value); }
    public string? Caption => (string?)GetValue(CaptionProperty);
    internal bool IsDropDownOpen => popup?.IsOpen == true;
    internal event EventHandler? SelectionChanged;
    internal event EventHandler? Opening;

    internal PickerOption? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (selectedItem == value)
                return;

            selectedItem = value;
            UpdateCaption();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void SetItems(IReadOnlyList<PickerOption> values)
    {
        items = values;
        if (IsDropDownOpen)
            Filter();
    }

    internal void SelectValue(string? value) => SelectedItem = items.FirstOrDefault(option => option.Value == value);

    private void UpdateCaption()
    {
        string? previous = Caption;
        SetValue(CaptionPropertyKey, DisplayText ?? selectedItem?.Label);
        if (UIElementAutomationPeer.FromElement(this) is SearchPickerPeer peer)
            peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, previous ?? "", Caption ?? "");
    }

    public override void OnApplyTemplate()
    {
        if (anchor is not null)
            anchor.Click -= AnchorClicked;
        if (popupContent is not null)
            popupContent.PreviewKeyDown -= PopupKeyDown;
        if (popup is not null)
        {
            popup.IsOpen = false;
            popup.Opened -= PopupOpened;
            popup.Closed -= PopupClosed;
        }
        if (search is not null)
            search.TextChanged -= SearchChanged;
        if (options is not null)
            options.PreviewMouseLeftButtonUp -= OptionClicked;

        base.OnApplyTemplate();
        anchor = GetTemplateChild("PART_Anchor") as Button;
        popup = GetTemplateChild("PART_Popup") as Popup;
        popupContent = popup?.Child as FrameworkElement;
        search = GetTemplateChild("PART_Search") as TextBox;
        options = GetTemplateChild("PART_Options") as ListBox;
        if (anchor is not null)
            anchor.Click += AnchorClicked;
        if (popupContent is not null)
            popupContent.PreviewKeyDown += PopupKeyDown;
        if (popup is not null)
        {
            popup.Opened += PopupOpened;
            popup.Closed += PopupClosed;
        }
        if (search is not null)
            search.TextChanged += SearchChanged;
        if (options is not null)
            options.PreviewMouseLeftButtonUp += OptionClicked;
    }

    internal void Open()
    {
        if (!IsEnabled || !IsVisible || IsDropDownOpen)
            return;

        ApplyTemplate();
        if (popup is null || search is null)
            return;

        Opening?.Invoke(this, EventArgs.Empty);
        search.Clear();
        Filter();
        popup.IsOpen = true;
    }

    internal void Close(bool restoreFocus = false)
    {
        if (popup is null || !popup.IsOpen)
            return;

        popup.IsOpen = false;
        if (restoreFocus && IsVisible)
            Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!IsDropDownOpen && (e.Key is Key.Space or Key.Enter or Key.Down or Key.Up or Key.F4 ||
            (e.Key == Key.System && e.SystemKey == Key.Down)))
        {
            e.Handled = true;
            Open();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SearchPickerPeer(this);

    private void AnchorClicked(object sender, RoutedEventArgs e)
    {
        if (suppressAnchorClick)
        {
            suppressAnchorClick = false;
            return;
        }

        if (IsDropDownOpen)
            Close(restoreFocus: true);
        else
            Open();
    }

    private void PopupOpened(object? sender, EventArgs e)
    {
        AnnounceExpansion(ExpandCollapseState.Collapsed, ExpandCollapseState.Expanded);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsDropDownOpen)
            {
                search?.Focus();
                if (options?.SelectedItem is { } selected)
                    options.ScrollIntoView(selected);
            }
        });
    }

    private void PopupClosed(object? sender, EventArgs e)
    {
        AnnounceExpansion(ExpandCollapseState.Expanded, ExpandCollapseState.Collapsed);
        // A click on the anchor may dismiss the mouse-capturing popup before Button.Click.
        suppressAnchorClick = Mouse.LeftButton == MouseButtonState.Pressed && anchor?.IsMouseOver == true;
        if (suppressAnchorClick)
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                if (Mouse.LeftButton == MouseButtonState.Released)
                    suppressAnchorClick = false;
            });
    }

    private void AnnounceExpansion(ExpandCollapseState previous, ExpandCollapseState current)
    {
        if (UIElementAutomationPeer.FromElement(this) is SearchPickerPeer peer)
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, previous, current);
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => Filter();

    private void Filter()
    {
        if (search is null || options is null)
            return;

        string query = search.Text.Trim();
        var filtered = query.Length == 0 ? items : items.Where(option =>
            option.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            option.Value.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        options.ItemsSource = filtered;
        options.Tag = selectedItem;
        options.SelectedItem = query.Length == 0 && filtered.Contains(selectedItem)
            ? selectedItem : filtered.FirstOrDefault();
    }

    private void PopupKeyDown(object sender, KeyEventArgs e)
    {
        if (options is null || search is null)
            return;

        if (e.Key == Key.Escape || e.Key == Key.F4 || (e.Key == Key.System && e.SystemKey == Key.Up))
            Close(restoreFocus: true);
        else if (e.Key == Key.Enter)
        {
            if (options.SelectedItem is PickerOption choice)
                Commit(choice);
        }
        else if (e.Key is Key.Down or Key.Up && search.IsKeyboardFocusWithin && options.Items.Count > 0)
        {
            int index = Math.Max(0, options.SelectedIndex);
            if (e.Key == Key.Up && index == 0)
                index = options.Items.Count - 1;
            options.SelectedIndex = index;
            options.ScrollIntoView(options.SelectedItem);
            options.UpdateLayout();
            (options.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
        }
        else
            return;

        e.Handled = true;
    }

    private void OptionClicked(object sender, MouseButtonEventArgs e)
    {
        if (options is not null && ItemsControl.ContainerFromElement(options, e.OriginalSource as DependencyObject) is
            ListBoxItem { Content: PickerOption choice })
        {
            e.Handled = true;
            Commit(choice);
        }
    }

    private void Commit(PickerOption choice)
    {
        Close(restoreFocus: true);
        SelectedItem = choice;
    }

    private sealed class SearchPickerPeer(SearchPicker owner) : FrameworkElementAutomationPeer(owner),
        IExpandCollapseProvider, IValueProvider
    {
        protected override string GetClassNameCore() => nameof(SearchPicker);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ComboBox;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface is PatternInterface.ExpandCollapse or PatternInterface.Value ? this : base.GetPattern(patternInterface);

        public ExpandCollapseState ExpandCollapseState => owner.IsDropDownOpen
            ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public void Expand()
        {
            if (!owner.IsEnabled)
                throw new ElementNotEnabledException();
            owner.Open();
        }

        public void Collapse() => owner.Close(restoreFocus: true);
        public string Value => owner.Caption ?? "";
        public bool IsReadOnly => true;
        public void SetValue(string value) => throw new InvalidOperationException();
    }
}

public sealed class SelectedOptionConverter : IMultiValueConverter
{
    public static SelectedOptionConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values[0] is PickerOption option && option.Equals(values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
