using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BabelRead.App.Controls;
using BabelRead.App.ViewModels;
using System.Threading;

namespace BabelRead.App.Views;

public partial class ReaderView : UserControl
{
    private const int ReflowDebounceMs = 250;

    // The reading inset and column cap — must match the PageText margin and MaxWidth in ReaderView.axaml,
    // since pagination subtracts them to know the space the text actually gets.
    private const double ReadingInsetX = 24;
    private const double ReadingInsetTop = 72;
    private const double ReadingInsetBottom = 56;
    private const double ReadingColumnMaxWidth = 720;

    // The floating toolbar hides this long after the reader stops moving the pointer.
    private static readonly TimeSpan ToolbarIdleTimeout = TimeSpan.FromSeconds(3);

    private readonly Panel _readingSurface;
    private readonly Border _toolbar;
    private readonly LinkableTextBlock _pageText;
    private readonly DispatcherTimer _toolbarHideTimer;
    private bool _pointerOverToolbar;
    private CancellationTokenSource? _reflowCts;
    private Size _lastReflowSize;

    /// <summary>Raised when the reader asks to open Settings; the host shows the settings window.</summary>
    public event EventHandler? OpenSettingsRequested;

    public ReaderView()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("OpenButton")!.Click += OnOpenClicked;
        this.FindControl<Button>("SettingsButton")!.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        _readingSurface = this.FindControl<Panel>("ReadingSurface")!;
        _toolbar = this.FindControl<Border>("Toolbar")!;
        _pageText = this.FindControl<LinkableTextBlock>("PageText")!;
        _pageText.LinkInvoked += (_, key) => _ = ViewModel?.FollowLinkAsync(key);

        // Re-measure the visual page only when the reading surface is actually resized. Reflowing on text
        // changes would feed back on itself: a landing translation changes the text, which changes the
        // layout, which would re-slice the page under the reader's eyes.
        SizeChanged += (_, _) => ScheduleReflow();
        _readingSurface.SizeChanged += (_, _) => ScheduleReflow();

        // Floating toolbar: reveal on pointer movement, hide again once the reader settles into reading.
        _toolbarHideTimer = new DispatcherTimer { Interval = ToolbarIdleTimeout };
        _toolbarHideTimer.Tick += (_, _) => { _toolbarHideTimer.Stop(); HideToolbarIfIdle(); };
        PointerMoved += (_, _) => RevealToolbar();
        _toolbar.PointerEntered += (_, _) => { _pointerOverToolbar = true; RevealToolbar(); };
        _toolbar.PointerExited += (_, _) => _pointerOverToolbar = false;

        Focusable = true;
        KeyDown += OnKeyDown;
    }

    /// <summary>Show the toolbar and restart the idle countdown that will hide it again.</summary>
    private void RevealToolbar()
    {
        _toolbar.Opacity = 1;
        _toolbar.IsHitTestVisible = true;

        // While there is no page to read, or the pointer is on the toolbar, keep it up.
        if (_pointerOverToolbar || ViewModel?.State != ReaderState.Content)
        {
            _toolbarHideTimer.Stop();
            return;
        }

        _toolbarHideTimer.Stop();
        _toolbarHideTimer.Start();
    }

    private void HideToolbarIfIdle()
    {
        if (_pointerOverToolbar || ViewModel?.State != ReaderState.Content)
        {
            return; // keep it visible until the reader is actually reading
        }

        _toolbar.Opacity = 0;
        _toolbar.IsHitTestVisible = false;
    }

    private ReaderViewModel? ViewModel => DataContext as ReaderViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Paging and zoom keys tunnel from the window so they win over whatever holds focus (a toolbar
        // button, the scroll viewer, the selectable text, or nothing at all).
        TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnReaderKeyDown, RoutingStrategies.Tunnel);
        _readingSurface.Focus();

        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnReaderKeyDown);

        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        void OnUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Post(action);
            }
        }

        // Bring the toolbar back whenever the reader isn't reading (opening, error, empty) so its controls
        // are never stranded behind an auto-hidden bar.
        if (e.PropertyName == nameof(ReaderViewModel.State))
        {
            OnUi(RevealToolbar);
        }
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a document",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Documents") { Patterns = ["*.pdf", "*.epub"] },
            ],
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
        {
            await ViewModel.OpenAsync(path);
            _readingSurface.Focus();
        }
    }

    private async void OnReaderKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
        {
            return; // never steal keys from a text field
        }

        // Ctrl (or Cmd) +/- zooms the reading font; Shift is tolerated since '+' is Shift+'=' on many layouts.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    e.Handled = true;
                    await ViewModel.IncreaseFontSizeAsync();
                    break;
                case Key.OemMinus or Key.Subtract:
                    e.Handled = true;
                    await ViewModel.DecreaseFontSizeAsync();
                    break;
            }

            return;
        }

        // Browser-style Back for a followed internal link. Alt+Left carries a modifier the guard below
        // would otherwise swallow, so it is handled here, ahead of it; plain Left still pages further down.
        if (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt)
        {
            if (ViewModel.CanGoBackFromLink)
            {
                e.Handled = true;
                await ViewModel.GoBackFromLinkAsync();
            }

            return;
        }

        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Back when ViewModel.CanGoBackFromLink:
                e.Handled = true;
                await ViewModel.GoBackFromLinkAsync();
                break;
            case Key.Left or Key.PageUp:
                e.Handled = true;
                await ViewModel.PreviousPageAsync();
                break;
            case Key.Right or Key.PageDown:
                e.Handled = true;
                await ViewModel.NextPageAsync();
                break;
        }
    }

    // Bubbling, so Space still activates a focused button before it reaches the toggle.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (e.Key is Key.T or Key.Space)
        {
            e.Handled = true;
            ViewModel.ToggleView();
        }
    }

    private void ScheduleReflow()
    {
        _reflowCts?.Cancel();
        _reflowCts?.Dispose();
        _reflowCts = new CancellationTokenSource();
        var token = _reflowCts.Token;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(ReflowDebounceMs, token);
                if (token.IsCancellationRequested || ViewModel is null)
                {
                    return;
                }

                // Subtract the text inset and cap the width at the centered column's measure, so the
                // paginator measures against exactly the space the text is drawn in. There is no scrollbar
                // now, so no gutter to reserve.
                var columnWidth = Math.Min(
                    _readingSurface.Bounds.Width - (ReadingInsetX * 2),
                    ReadingColumnMaxWidth);
                var size = new Size(columnWidth, _readingSurface.Bounds.Height - ReadingInsetTop - ReadingInsetBottom);

                if (size.Width <= 0 || size.Height <= 0)
                {
                    return;
                }

                if (Math.Abs(size.Width - _lastReflowSize.Width) < 1 && Math.Abs(size.Height - _lastReflowSize.Height) < 1)
                {
                    return; // same surface as last time — the page slice would not change
                }

                _lastReflowSize = size;
                var typeface = new Typeface(_pageText.FontFamily);
                ViewModel.SetReadingMetrics(size.Width, size.Height, typeface);
            }
            catch (OperationCanceledException)
            {
            }
        }, DispatcherPriority.Background);
    }
}
