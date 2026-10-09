namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// The glue between the mRemoteNG palette (Themes/DarkTheme.axaml, LightTheme.axaml) and the Fluent control
/// templates. Fluent's templates colour every visual state through their own resource keys
/// (<c>ButtonBackgroundPointerOver</c>, <c>TextControlBorderBrushFocused</c>, …); <see cref="ThemeService"/> points
/// those keys at the palette's brush instances when it loads a palette. Because the very same brush objects are
/// shared, recolouring a palette key in place (named themes, the theme editor's live preview) recolours every
/// control state that uses it, and the control styles in Themes/Controls.axaml only have to set geometry.
/// See docs/design-system.md §2 and §6.
/// </summary>
public static class ThemeTokens
{
    /// <summary>Fluent brush key → palette brush key (the alias shares the palette's brush instance).</summary>
    public static IReadOnlyDictionary<string, string> FluentBrushAliases { get; } = BuildBrushAliases();

    /// <summary>Fluent colour key → palette colour key (copied, and updated when the palette colour changes).</summary>
    public static IReadOnlyDictionary<string, string> FluentColorAliases { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SystemAccentColor"] = "Accent",
        ["SystemAccentColorLight1"] = "AccentHover",
        ["SystemAccentColorLight2"] = "AccentHover",
        ["SystemAccentColorLight3"] = "AccentHover",
        ["SystemAccentColorDark1"] = "AccentActive",
        ["SystemAccentColorDark2"] = "AccentActive",
        ["SystemAccentColorDark3"] = "AccentActive",
        ["TextControlSelectionHighlightColor"] = "Accent",
        ["DataGridRowHoveredBackgroundColor"] = "AppBg4",
    };

    private static Dictionary<string, string> BuildBrushAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        void Map(string token, params string[] fluentKeys)
        {
            foreach (var key in fluentKeys)
                map[key] = token + "Brush";
        }

        // Shared list/selection brushes (ListBoxItem, menus, pickers…)
        Map("AppBg4", "SystemControlHighlightListLowBrush");
        Map("AppBg3", "SystemControlHighlightListMediumBrush");
        Map("AccentSubtle", "SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush",
            "SystemControlHighlightListAccentHighBrush");
        Map("Accent", "SystemControlFocusVisualPrimaryBrush", "SystemControlHighlightAccentBrush", "SystemControlForegroundAccentBrush");
        Map("AppBg0", "SystemControlFocusVisualSecondaryBrush");
        Map("TextPrimary", "SystemControlForegroundBaseHighBrush");

        // Buttons, repeat buttons and toggle buttons: secondary look; disabled = same colours at 45 % opacity
        foreach (var prefix in new[] { "Button", "RepeatButton", "ToggleButton" })
        {
            Map("AppBg2", prefix + "Background", prefix + "BackgroundDisabled");
            Map("AppBg4", prefix + "BackgroundPointerOver");
            Map("AppBg3", prefix + "BackgroundPressed");
            Map("Border1", prefix + "BorderBrush", prefix + "BorderBrushPointerOver", prefix + "BorderBrushPressed", prefix + "BorderBrushDisabled");
            Map("TextPrimary", prefix + "Foreground", prefix + "ForegroundPointerOver", prefix + "ForegroundPressed", prefix + "ForegroundDisabled");
        }

        // Fluent's built-in Button.accent theme (Classes="accent")
        Map("Accent", "AccentButtonBackground", "AccentButtonBackgroundDisabled", "AccentButtonBorderBrush", "AccentButtonBorderBrushDisabled");
        Map("AccentHover", "AccentButtonBackgroundPointerOver", "AccentButtonBorderBrushPointerOver");
        Map("AccentActive", "AccentButtonBackgroundPressed", "AccentButtonBorderBrushPressed");
        Map("OnAccent", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed",
            "AccentButtonForegroundDisabled");

        Map("AccentSubtle", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPressed",
            "ToggleButtonBackgroundCheckedDisabled", "ToggleButtonBackgroundIndeterminate", "ToggleButtonBackgroundIndeterminatePointerOver",
            "ToggleButtonBackgroundIndeterminatePressed", "ToggleButtonBackgroundIndeterminateDisabled");
        Map("Accent", "ToggleButtonBorderBrushChecked", "ToggleButtonBorderBrushCheckedPointerOver", "ToggleButtonBorderBrushCheckedPressed",
            "ToggleButtonBorderBrushCheckedDisabled", "ToggleButtonBorderBrushIndeterminate", "ToggleButtonBorderBrushIndeterminatePointerOver",
            "ToggleButtonBorderBrushIndeterminatePressed", "ToggleButtonBorderBrushIndeterminateDisabled");
        Map("TextPrimary", "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed",
            "ToggleButtonForegroundCheckedDisabled", "ToggleButtonForegroundIndeterminate", "ToggleButtonForegroundIndeterminatePointerOver",
            "ToggleButtonForegroundIndeterminatePressed", "ToggleButtonForegroundIndeterminateDisabled");

        // TextBox, NumericUpDown, AutoCompleteBox…
        Map("AppBg2", "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled");
        Map("Border1", "TextControlBorderBrush");
        Map("TextMuted", "TextControlBorderBrushPointerOver");
        Map("Accent", "TextControlBorderBrushFocused");
        Map("Border0", "TextControlBorderBrushDisabled");
        Map("TextPrimary", "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused");
        Map("TextMuted", "TextControlForegroundDisabled", "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver",
            "TextControlPlaceholderForegroundFocused", "TextControlPlaceholderForegroundDisabled");
        Map("TextSecondary", "TextControlButtonForeground");
        Map("TextPrimary", "TextControlButtonForegroundPointerOver", "TextControlButtonForegroundPressed");
        Map("AppBg4", "TextControlButtonBackgroundPointerOver");
        Map("AppBg3", "TextControlButtonBackgroundPressed");

        // ComboBox and its items
        Map("AppBg2", "ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundDisabled", "ComboBoxBackgroundUnfocused");
        Map("AppBg3", "ComboBoxBackgroundPressed");
        Map("Border1", "ComboBoxBorderBrush");
        Map("TextMuted", "ComboBoxBorderBrushPointerOver");
        Map("Accent", "ComboBoxBorderBrushPressed", "ComboBoxBackgroundBorderBrushFocused", "ComboBoxBackgroundBorderBrushUnfocused");
        Map("Border0", "ComboBoxBorderBrushDisabled");
        Map("TextPrimary", "ComboBoxForeground", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed");
        Map("TextMuted", "ComboBoxForegroundDisabled", "ComboBoxPlaceHolderForeground", "ComboBoxPlaceHolderForegroundFocusedPressed",
            "ComboBoxDropDownGlyphForegroundDisabled");
        Map("TextSecondary", "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed");
        Map("AppBg1", "ComboBoxDropDownBackground");
        Map("Border1", "ComboBoxDropDownBorderBrush");
        Map("AppBg4", "ComboBoxItemBackgroundPointerOver");
        Map("AppBg3", "ComboBoxItemBackgroundPressed");
        Map("AccentSubtle", "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPressed",
            "ComboBoxItemBackgroundSelectedDisabled");
        Map("TextPrimary", "ComboBoxItemForeground", "ComboBoxItemForegroundPointerOver", "ComboBoxItemForegroundPressed",
            "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedPointerOver", "ComboBoxItemForegroundSelectedPressed");
        Map("TextMuted", "ComboBoxItemForegroundDisabled", "ComboBoxItemForegroundSelectedDisabled");

        // TreeView rows
        Map("AppBg4", "TreeViewItemBackgroundPointerOver");
        Map("AppBg3", "TreeViewItemBackgroundPressed");
        Map("AccentSubtle", "TreeViewItemBackgroundSelected", "TreeViewItemBackgroundSelectedPointerOver", "TreeViewItemBackgroundSelectedPressed",
            "TreeViewItemBackgroundSelectedDisabled");
        Map("TextPrimary", "TreeViewItemForeground", "TreeViewItemForegroundPointerOver", "TreeViewItemForegroundPressed",
            "TreeViewItemForegroundSelected", "TreeViewItemForegroundSelectedPointerOver", "TreeViewItemForegroundSelectedPressed");
        Map("TextMuted", "TreeViewItemForegroundDisabled", "TreeViewItemForegroundSelectedDisabled");

        // Menus, context menus and flyouts
        Map("AppBg1", "MenuFlyoutPresenterBackground", "FlyoutPresenterBackground");
        Map("Border1", "MenuFlyoutPresenterBorderBrush", "FlyoutBorderThemeBrush");
        Map("AppBg4", "MenuFlyoutItemBackgroundPointerOver");
        Map("AppBg3", "MenuFlyoutItemBackgroundPressed");
        Map("TextPrimary", "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed");
        Map("TextMuted", "MenuFlyoutItemForegroundDisabled", "MenuFlyoutItemKeyboardAcceleratorTextForeground",
            "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed",
            "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "MenuFlyoutSubItemChevron", "MenuFlyoutSubItemChevronPointerOver",
            "MenuFlyoutSubItemChevronPressed", "MenuFlyoutSubItemChevronSubMenuOpened", "MenuFlyoutSubItemChevronDisabled");

        // CheckBox: 18 px box, accent fill when checked
        foreach (var state in new[] { "Unchecked", "Checked", "Indeterminate" })
        {
            Map("TextPrimary", $"CheckBoxForeground{state}", $"CheckBoxForeground{state}PointerOver", $"CheckBoxForeground{state}Pressed",
                $"CheckBoxForeground{state}Disabled");
        }

        Map("AppBg2", "CheckBoxCheckBackgroundFillUnchecked", "CheckBoxCheckBackgroundFillUncheckedDisabled");
        Map("AppBg4", "CheckBoxCheckBackgroundFillUncheckedPointerOver");
        Map("AppBg3", "CheckBoxCheckBackgroundFillUncheckedPressed");
        Map("TextMuted", "CheckBoxCheckBackgroundStrokeUnchecked", "CheckBoxCheckBackgroundStrokeUncheckedDisabled");
        Map("TextSecondary", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver", "CheckBoxCheckBackgroundStrokeUncheckedPressed");
        foreach (var state in new[] { "Checked", "Indeterminate" })
        {
            Map("Accent", $"CheckBoxCheckBackgroundFill{state}", $"CheckBoxCheckBackgroundFill{state}Disabled",
                $"CheckBoxCheckBackgroundStroke{state}", $"CheckBoxCheckBackgroundStroke{state}Disabled");
            Map("AccentHover", $"CheckBoxCheckBackgroundFill{state}PointerOver", $"CheckBoxCheckBackgroundStroke{state}PointerOver");
            Map("AccentActive", $"CheckBoxCheckBackgroundFill{state}Pressed", $"CheckBoxCheckBackgroundStroke{state}Pressed");
            Map("OnAccent", $"CheckBoxCheckGlyphForeground{state}", $"CheckBoxCheckGlyphForeground{state}PointerOver",
                $"CheckBoxCheckGlyphForeground{state}Pressed", $"CheckBoxCheckGlyphForeground{state}Disabled");
        }

        // RadioButton
        Map("TextPrimary", "RadioButtonForeground", "RadioButtonForegroundPointerOver", "RadioButtonForegroundPressed", "RadioButtonForegroundDisabled");
        Map("TextMuted", "RadioButtonOuterEllipseStroke", "RadioButtonOuterEllipseStrokeDisabled");
        Map("TextSecondary", "RadioButtonOuterEllipseStrokePointerOver", "RadioButtonOuterEllipseStrokePressed");
        Map("AppBg2", "RadioButtonOuterEllipseFill", "RadioButtonOuterEllipseFillDisabled");
        Map("AppBg4", "RadioButtonOuterEllipseFillPointerOver");
        Map("AppBg3", "RadioButtonOuterEllipseFillPressed");
        Map("Accent", "RadioButtonOuterEllipseCheckedStroke", "RadioButtonOuterEllipseCheckedFill",
            "RadioButtonOuterEllipseCheckedStrokeDisabled", "RadioButtonOuterEllipseCheckedFillDisabled");
        Map("AccentHover", "RadioButtonOuterEllipseCheckedStrokePointerOver", "RadioButtonOuterEllipseCheckedFillPointerOver");
        Map("AccentActive", "RadioButtonOuterEllipseCheckedStrokePressed", "RadioButtonOuterEllipseCheckedFillPressed");
        Map("OnAccent", "RadioButtonCheckGlyphFill", "RadioButtonCheckGlyphFillPointerOver", "RadioButtonCheckGlyphFillPressed",
            "RadioButtonCheckGlyphFillDisabled", "RadioButtonCheckGlyphStroke", "RadioButtonCheckGlyphStrokePointerOver",
            "RadioButtonCheckGlyphStrokePressed", "RadioButtonCheckGlyphStrokeDisabled");

        // ToggleSwitch
        Map("TextPrimary", "ToggleSwitchContentForeground", "ToggleSwitchHeaderForeground");
        Map("TextMuted", "ToggleSwitchHeaderForegroundDisabled");
        Map("AppBg2", "ToggleSwitchFillOff", "ToggleSwitchFillOffDisabled");
        Map("AppBg4", "ToggleSwitchFillOffPointerOver");
        Map("AppBg3", "ToggleSwitchFillOffPressed");
        Map("TextMuted", "ToggleSwitchStrokeOff", "ToggleSwitchStrokeOffDisabled");
        Map("TextSecondary", "ToggleSwitchStrokeOffPointerOver", "ToggleSwitchStrokeOffPressed", "ToggleSwitchKnobFillOff",
            "ToggleSwitchKnobFillOffDisabled");
        Map("TextPrimary", "ToggleSwitchKnobFillOffPointerOver", "ToggleSwitchKnobFillOffPressed");
        Map("Accent", "ToggleSwitchFillOn", "ToggleSwitchFillOnDisabled", "ToggleSwitchStrokeOn", "ToggleSwitchStrokeOnDisabled");
        Map("AccentHover", "ToggleSwitchFillOnPointerOver", "ToggleSwitchStrokeOnPointerOver");
        Map("AccentActive", "ToggleSwitchFillOnPressed", "ToggleSwitchStrokeOnPressed");
        Map("OnAccent", "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed", "ToggleSwitchKnobFillOnDisabled");

        // Slider
        Map("Accent", "SliderTrackValueFill", "SliderTrackValueFillDisabled", "SliderThumbBackground", "SliderThumbBackgroundDisabled");
        Map("AccentHover", "SliderTrackValueFillPointerOver", "SliderThumbBackgroundPointerOver");
        Map("AccentActive", "SliderTrackValueFillPressed", "SliderThumbBackgroundPressed");
        Map("Border1", "SliderTrackFill", "SliderTrackFillDisabled");
        Map("TextMuted", "SliderTrackFillPointerOver", "SliderTrackFillPressed");

        // ScrollBar (thin, auto-hiding)
        Map("TextMuted", "ScrollBarPanningThumbBackground", "ScrollBarThumbFillPointerOver");
        Map("TextSecondary", "ScrollBarThumbFillPressed");
        Map("Border0", "ScrollBarThumbFillDisabled");
        Map("TextMuted", "ScrollBarButtonArrowForeground");
        Map("TextPrimary", "ScrollBarButtonArrowForegroundPointerOver", "ScrollBarButtonArrowForegroundPressed");

        // TabControl (generic underline tabs; session tabs have their own look)
        Map("TextSecondary", "TabItemHeaderForegroundUnselected");
        Map("TextPrimary", "TabItemHeaderForegroundUnselectedPointerOver", "TabItemHeaderForegroundUnselectedPressed",
            "TabItemHeaderForegroundSelected", "TabItemHeaderForegroundSelectedPointerOver", "TabItemHeaderForegroundSelectedPressed");
        Map("TextMuted", "TabItemHeaderForegroundDisabled");
        Map("Accent", "TabItemHeaderSelectedPipeFill");

        // Expander
        Map("AppBg1", "ExpanderHeaderBackground", "ExpanderHeaderBackgroundDisabled", "ExpanderContentBackground");
        Map("AppBg4", "ExpanderHeaderBackgroundPointerOver", "ExpanderChevronBackgroundPointerOver");
        Map("AppBg3", "ExpanderHeaderBackgroundPressed", "ExpanderChevronBackgroundPressed");
        Map("Border0", "ExpanderHeaderBorderBrush", "ExpanderHeaderBorderBrushPointerOver", "ExpanderHeaderBorderBrushPressed",
            "ExpanderHeaderBorderBrushDisabled", "ExpanderContentBorderBrush", "ExpanderChevronBorderBrush",
            "ExpanderChevronBorderBrushPointerOver", "ExpanderChevronBorderBrushPressed", "ExpanderChevronBorderBrushDisabled");
        Map("TextPrimary", "ExpanderHeaderForeground", "ExpanderHeaderForegroundPointerOver", "ExpanderHeaderForegroundPressed");
        Map("TextMuted", "ExpanderHeaderForegroundDisabled", "ExpanderChevronForegroundDisabled");
        Map("TextSecondary", "ExpanderChevronForeground");
        Map("TextPrimary", "ExpanderChevronForegroundPointerOver", "ExpanderChevronForegroundPressed");

        // DataGrid (header row, row hover/selection, grid lines)
        Map("AppBg2", "DataGridColumnHeaderBackgroundBrush");
        Map("AppBg4", "DataGridColumnHeaderHoveredBackgroundBrush", "DataGridRowGroupHeaderHoveredBackgroundBrush");
        Map("AppBg3", "DataGridColumnHeaderPressedBackgroundBrush", "DataGridColumnHeaderDraggedBackgroundBrush",
            "DataGridRowGroupHeaderPressedBackgroundBrush");
        Map("TextSecondary", "DataGridColumnHeaderForegroundBrush");
        Map("Border0", "DataGridGridLinesBrush", "DataGridFillerColumnGridLinesBrush");
        Map("AccentSubtle", "DataGridRowSelectedBackgroundBrush", "DataGridRowSelectedUnfocusedBackgroundBrush",
            "DataGridRowSelectedHoveredBackgroundBrush", "DataGridRowSelectedHoveredUnfocusedBackgroundBrush");
        Map("Accent", "DataGridCellFocusVisualPrimaryBrush", "DataGridCurrencyVisualPrimaryBrush", "DataGridDropLocationIndicatorBackground");
        Map("AppBg1", "DataGridDetailsPresenterBackgroundBrush", "DataGridScrollBarsSeparatorBackground", "DataGridRowGroupHeaderBackgroundBrush");
        Map("TextPrimary", "DataGridRowGroupHeaderForegroundBrush");
        Map("Danger", "DataGridCellInvalidBrush", "DataGridRowInvalidBrush");

        return map;
    }
}
