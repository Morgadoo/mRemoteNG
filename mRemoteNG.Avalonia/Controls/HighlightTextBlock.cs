using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace mRemoteNG.Avalonia.Controls;

/// <summary>
/// A TextBlock that emphasises every case-insensitive occurrence of <see cref="Highlight"/> in its text (search
/// results in the connection tree). The matches use the <c>search-match</c> look: SemiBold in
/// <see cref="HighlightForeground"/> on <see cref="HighlightBackground"/> (set from the palette by the styles).
/// <code>&lt;ctl:HighlightTextBlock Text="{Binding Name}" Highlight="{Binding SearchFilter}"/&gt;</code>
/// Its style key is its own type: style it with <c>ctl|HighlightTextBlock</c> or <c>:is(TextBlock)</c> selectors.
/// </summary>
public class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> HighlightProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Highlight));

    public static readonly StyledProperty<IBrush?> HighlightBackgroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(HighlightBackground));

    public static readonly StyledProperty<IBrush?> HighlightForegroundProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(HighlightForeground));

    private bool _updating;

    /// <summary>The text to emphasise; nothing is emphasised when it is blank.</summary>
    public string? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public IBrush? HighlightBackground
    {
        get => GetValue(HighlightBackgroundProperty);
        set => SetValue(HighlightBackgroundProperty, value);
    }

    public IBrush? HighlightForeground
    {
        get => GetValue(HighlightForegroundProperty);
        set => SetValue(HighlightForegroundProperty, value);
    }

    /// <summary>The (start, length) ranges of <paramref name="text"/> that match <paramref name="term"/>.</summary>
    public static IReadOnlyList<(int Start, int Length)> FindMatches(string? text, string? term)
    {
        var matches = new List<(int, int)>();
        term = term?.Trim();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
            return matches;
        var index = 0;
        while ((index = text.IndexOf(term, index, StringComparison.CurrentCultureIgnoreCase)) >= 0)
        {
            matches.Add((index, term.Length));
            index += term.Length;
        }
        return matches;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_updating) return;
        if (change.Property == TextProperty || change.Property == HighlightProperty
            || change.Property == HighlightBackgroundProperty || change.Property == HighlightForegroundProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        var text = Text;
        var matches = FindMatches(text, Highlight);
        _updating = true;
        try
        {
            if (matches.Count == 0)
            {
                // Plain text: drop the runs (setting Text again clears the inlines).
                if (Inlines is { Count: > 0 })
                {
                    Inlines.Clear();
                    SetCurrentValue(TextProperty, text);
                }
                return;
            }

            var inlines = new InlineCollection();
            var position = 0;
            foreach (var (start, length) in matches)
            {
                if (start > position)
                    inlines.Add(new Run(text![position..start]));
                var match = new Run(text![start..(start + length)]) { FontWeight = FontWeight.SemiBold };
                // Unset brushes inherit the text's own colour (never a null brush, which draws nothing).
                if (HighlightBackground is { } background)
                    match.Background = background;
                if (HighlightForeground is { } foreground)
                    match.Foreground = foreground;
                inlines.Add(match);
                position = start + length;
            }
            if (position < text!.Length)
                inlines.Add(new Run(text[position..]));
            Inlines = inlines;
        }
        finally
        {
            _updating = false;
        }
    }
}
