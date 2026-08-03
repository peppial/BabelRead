using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using VisibleLink = BabelRead.App.ViewModels.ReaderViewModel.VisibleLink;

namespace BabelRead.App.Controls;

/// <summary>A <see cref="SelectableTextBlock"/> that renders internal-hyperlink runs as underlined,
/// accent-colored text and raises <see cref="LinkInvoked"/> when one is clicked. Used in the original
/// view only (<see cref="LinksEnabled"/> tracks <c>!ShowingTranslation</c>) — the translation view keeps
/// the plain <see cref="TextBlock.Text"/> path, unaffected by <see cref="Links"/>.</summary>
public sealed class LinkableTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<VisibleLink>?> LinksProperty =
        AvaloniaProperty.Register<LinkableTextBlock, IReadOnlyList<VisibleLink>?>(nameof(Links));

    public static readonly StyledProperty<bool> LinksEnabledProperty =
        AvaloniaProperty.Register<LinkableTextBlock, bool>(nameof(LinksEnabled));

    /// <summary>Internal-hyperlink ranges within the current <see cref="TextBlock.Text"/>, relative to it.</summary>
    public IReadOnlyList<VisibleLink>? Links
    {
        get => GetValue(LinksProperty);
        set => SetValue(LinksProperty, value);
    }

    /// <summary>Whether link runs should be drawn (and be clickable). False in the translation view.</summary>
    public bool LinksEnabled
    {
        get => GetValue(LinksEnabledProperty);
        set => SetValue(LinksEnabledProperty, value);
    }

    /// <summary>Raised with a link's target key when the reader clicks it.</summary>
    public event EventHandler<string>? LinkInvoked;

    private Point _pressPoint;

    static LinkableTextBlock()
    {
        // Rebuild the rendered runs whenever the underlying text or the link set changes.
        TextProperty.Changed.AddClassHandler<LinkableTextBlock>((c, _) => c.Rebuild());
        LinksProperty.Changed.AddClassHandler<LinkableTextBlock>((c, _) => c.Rebuild());
        LinksEnabledProperty.Changed.AddClassHandler<LinkableTextBlock>((c, _) => c.Rebuild());
    }

    /// <summary>Non-empty <see cref="TextBlock.Inlines"/> take precedence over <see cref="TextBlock.Text"/>
    /// in Avalonia, so links are rendered by building runs into <c>Inlines</c>; the plain path (no links, or
    /// the translation view) clears <c>Inlines</c> so <c>Text</c> shows again.</summary>
    private void Rebuild()
    {
        var text = Text ?? string.Empty;
        var links = LinksEnabled ? Links : null;
        if (links is not { Count: > 0 })
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

    /// <summary>A click (not a drag-select) inside an underlined run's text position raises
    /// <see cref="LinkInvoked"/>. Selection still works normally otherwise.</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!LinksEnabled || Links is not { Count: > 0 } links)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _pressPoint.X) > 3 || Math.Abs(point.Y - _pressPoint.Y) > 3)
        {
            return; // a drag/selection, not a click
        }

        if (!string.IsNullOrEmpty(SelectedText))
        {
            return;
        }

        var hit = TextLayout.HitTestPoint(point);
        if (!hit.IsInside)
        {
            return;
        }

        foreach (var link in links)
        {
            if (hit.TextPosition >= link.Start && hit.TextPosition < link.Start + link.Length)
            {
                LinkInvoked?.Invoke(this, link.TargetKey);
                e.Handled = true;
                return;
            }
        }
    }
}
