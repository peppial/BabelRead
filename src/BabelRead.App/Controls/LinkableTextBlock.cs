using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using VisibleLink = BabelRead.App.ViewModels.ReaderViewModel.VisibleLink;

namespace BabelRead.App.Controls;

/// <summary>A <see cref="SelectableTextBlock"/> that renders internal-hyperlink runs as underlined,
/// accent-colored text and raises <see cref="LinkInvoked"/> when one is clicked. What is linkable is
/// entirely the view-model's call: it renders exactly the <see cref="Links"/> it is handed, and falls back
/// to the plain <see cref="TextBlock.Text"/> path when handed none.</summary>
public sealed class LinkableTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<VisibleLink>?> LinksProperty =
        AvaloniaProperty.Register<LinkableTextBlock, IReadOnlyList<VisibleLink>?>(nameof(Links));

    /// <summary>Internal-hyperlink ranges within the current <see cref="TextBlock.Text"/>, relative to it.</summary>
    public IReadOnlyList<VisibleLink>? Links
    {
        get => GetValue(LinksProperty);
        set => SetValue(LinksProperty, value);
    }

    /// <summary>Raised with a link's target key when the reader clicks it.</summary>
    public event EventHandler<string>? LinkInvoked;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private Point _pressPoint;
    private bool _showingLinkCursor;

    static LinkableTextBlock()
    {
        // A text block with no Background is hit-test transparent: pointer events pass straight through it
        // to whatever is behind, so neither a link click nor a selection drag would ever reach this control.
        BackgroundProperty.OverrideDefaultValue<LinkableTextBlock>(Brushes.Transparent);

        // Rebuild the rendered runs whenever the underlying text or the link set changes.
        TextProperty.Changed.AddClassHandler<LinkableTextBlock>((c, _) => c.Rebuild());
        LinksProperty.Changed.AddClassHandler<LinkableTextBlock>((c, _) => c.Rebuild());
    }

    /// <summary>Non-empty <see cref="TextBlock.Inlines"/> take precedence over <see cref="TextBlock.Text"/>
    /// in Avalonia, so links are rendered by building runs into <c>Inlines</c>; the plain path (no links, or
    /// the translation view) clears <c>Inlines</c> so <c>Text</c> shows again.</summary>
    private void Rebuild()
    {
        var text = Text ?? string.Empty;
        if (Links is not { Count: > 0 } links)
        {
            Inlines?.Clear();
            SetCurrentValue(TextProperty, text); // plain path
            return;
        }

        IBrush accent = this.TryFindResource("SystemAccentColor", out var resource) && resource is Color color
            ? new SolidColorBrush(color)
            : Brushes.SteelBlue;

        var inlines = new InlineCollection();
        var cursor = 0;
        foreach (var link in links.OrderBy(l => l.Start))
        {
            var start = Math.Clamp(link.Start, 0, text.Length);
            var end = Math.Clamp(start + link.Length, start, text.Length);
            if (start > cursor)
            {
                inlines.Add(new Run(text[cursor..start]));
            }

            var linkRun = new Run(text[start..end])
            {
                Foreground = accent,
                TextDecorations = Avalonia.Media.TextDecorations.Underline,
            };
            inlines.Add(linkRun);
            cursor = end;
        }

        if (cursor < text.Length)
        {
            inlines.Add(new Run(text[cursor..]));
        }

        var current = Inlines!;
        current.Clear();
        current.AddRange(inlines);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        base.OnPointerPressed(e);
    }

    /// <summary>A click (not a drag-select) inside an underlined run raises <see cref="LinkInvoked"/>.
    /// Selection still works normally otherwise.</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _pressPoint.X) > 3 || Math.Abs(point.Y - _pressPoint.Y) > 3)
        {
            return; // a drag/selection, not a click
        }

        if (!string.IsNullOrEmpty(SelectedText))
        {
            return;
        }

        if (LinkAt(point) is { } link)
        {
            LinkInvoked?.Invoke(this, link.TargetKey);
            e.Handled = true;
        }
    }

    /// <summary>Over a link the pointer becomes a hand, the signal that the text can be clicked; elsewhere
    /// the control's own cursor (the I-beam that says the text can be selected) is left alone.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ShowLinkCursor(LinkAt(e.GetPosition(this)) is not null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ShowLinkCursor(false);
    }

    private void ShowLinkCursor(bool overLink)
    {
        if (overLink == _showingLinkCursor)
        {
            return;
        }

        _showingLinkCursor = overLink;
        if (overLink)
        {
            SetCurrentValue(CursorProperty, HandCursor);
        }
        else
        {
            ClearValue(CursorProperty); // back to whatever the theme dresses selectable text in
        }
    }

    /// <summary>The link drawn under <paramref name="point"/>, if any. Asks the layout for the rectangles
    /// each link's text actually occupies: resolving the point to a text position instead would mean
    /// trusting <c>TextHitTestResult.IsInside</c> to say whether it is on text at all, and that reports
    /// false for points squarely on a glyph once the reader's line height is applied.</summary>
    private VisibleLink? LinkAt(Point point)
    {
        if (Links is not { Count: > 0 } links)
        {
            return null;
        }

        var length = (Text ?? string.Empty).Length;
        foreach (var link in links)
        {
            var start = Math.Clamp(link.Start, 0, length);
            var end = Math.Clamp(start + link.Length, start, length);
            if (end == start)
            {
                continue;
            }

            foreach (var rect in TextLayout.HitTestTextRange(start, end - start)) // one per line it wraps over
            {
                if (rect.Contains(point))
                {
                    return link;
                }
            }
        }

        return null;
    }
}
