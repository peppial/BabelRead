using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BabelRead.App.Controls;

/// <summary>Reusable panel for loading / empty / error states with an optional retry action,
/// so every non-content state is presented consistently (Constitution III; FR-009/FR-011).</summary>
public partial class StatePanel : UserControl
{
    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<StatePanel, string?>(nameof(Message));

    public static readonly StyledProperty<bool> ShowRetryProperty =
        AvaloniaProperty.Register<StatePanel, bool>(nameof(ShowRetry));

    public static readonly StyledProperty<ICommand?> RetryCommandProperty =
        AvaloniaProperty.Register<StatePanel, ICommand?>(nameof(RetryCommand));

    private readonly TextBlock _messageText;
    private readonly Button _retryButton;

    public StatePanel()
    {
        AvaloniaXamlLoader.Load(this);
        _messageText = this.FindControl<TextBlock>("MessageText")!;
        _retryButton = this.FindControl<Button>("RetryButton")!;
    }

    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public bool ShowRetry
    {
        get => GetValue(ShowRetryProperty);
        set => SetValue(ShowRetryProperty, value);
    }

    public ICommand? RetryCommand
    {
        get => GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MessageProperty)
        {
            _messageText.Text = Message;
        }
        else if (change.Property == ShowRetryProperty)
        {
            _retryButton.IsVisible = ShowRetry;
        }
        else if (change.Property == RetryCommandProperty)
        {
            _retryButton.Command = RetryCommand;
        }
    }
}
