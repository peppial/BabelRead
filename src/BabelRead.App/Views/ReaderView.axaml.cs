using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BabelRead.App.ViewModels;
using System.Threading;

namespace BabelRead.App.Views;

public partial class ReaderView : UserControl
{
    private const int ReflowDebounceMs = 250;

    private readonly ScrollViewer _readingScroll;
    private readonly SelectableTextBlock _pageText;
    private CancellationTokenSource? _reflowCts;

    /// <summary>Raised when the reader asks to open Settings; the host shows the settings window.</summary>
    public event EventHandler? OpenSettingsRequested;

    public ReaderView()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("OpenButton")!.Click += OnOpenClicked;
        this.FindControl<Button>("SettingsButton")!.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        _readingScroll = this.FindControl<ScrollViewer>("ReadingScroll")!;
        _pageText = this.FindControl<SelectableTextBlock>("PageText")!;

        SizeChanged += (_, _) => ScheduleReflow();
        _readingScroll.SizeChanged += (_, _) => ScheduleReflow();
        _pageText.PropertyChanged += (_, e) =>
        {
            if (e.Property == SelectableTextBlock.TextProperty || e.Property == BoundsProperty)
            {
                ScheduleReflow();
            }
        };
        _readingScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == BoundsProperty)
            {
                ScheduleReflow();
            }
        };
        Focusable = true;
        KeyDown += OnKeyDown;
    }

    private ReaderViewModel? ViewModel => DataContext as ReaderViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Paging and zoom keys tunnel from the window so they win over whatever holds focus (a toolbar
        // button, the scroll viewer, the selectable text, or nothing at all).
        TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnReaderKeyDown, RoutingStrategies.Tunnel);
        _readingScroll.Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnReaderKeyDown);
        base.OnDetachedFromVisualTree(e);
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
            _readingScroll.Focus();
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

        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
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
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (ViewModel is null || !_readingScroll.IsVisible || _readingScroll.Viewport.Width <= 0 || _readingScroll.Viewport.Height <= 0)
                {
                    return;
                }

                await ViewModel.ReflowForViewportAsync(_readingScroll.Viewport.Width, _readingScroll.Viewport.Height);
            }
            catch (OperationCanceledException)
            {
            }
        }, DispatcherPriority.Background);
    }
}
