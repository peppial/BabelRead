using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using BabelRead.App.ViewModels;

namespace BabelRead.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("UseSelectedButton")!.Click += OnUseSelected;
        this.FindControl<Button>("AddCloudButton")!.Click += OnAddCloud;
        this.FindControl<Button>("ApplyTargetButton")!.Click += OnApplyTarget;
        this.FindControl<Button>("ApplySourceButton")!.Click += OnApplySource;
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private async void OnUseSelected(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.ActiveProfile is { } profile)
        {
            await ViewModel.SetActiveAsync(profile.ProfileId);
        }
    }

    private async void OnAddCloud(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var name = this.FindControl<TextBox>("CloudName")!.Text ?? string.Empty;
        var modelId = this.FindControl<TextBox>("CloudModelId")!.Text ?? string.Empty;
        var endpointText = this.FindControl<TextBox>("CloudEndpoint")!.Text;
        var apiKey = this.FindControl<TextBox>("CloudApiKey")!.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        Uri? endpoint = Uri.TryCreate(endpointText, UriKind.Absolute, out var uri) ? uri : null;
        var profileId = name.Trim().ToLowerInvariant().Replace(' ', '-');
        await ViewModel.AddCloudProfileAsync(profileId, name.Trim(), modelId.Trim(), endpoint, apiKey);
    }

    private async void OnApplyTarget(object? sender, RoutedEventArgs e)
    {
        var code = this.FindControl<TextBox>("TargetLanguage")!.Text;
        if (ViewModel is not null && !string.IsNullOrWhiteSpace(code))
        {
            await ViewModel.ApplyTargetLanguageAsync(code);
        }
    }

    private async void OnApplySource(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ApplySourceOverrideAsync(this.FindControl<TextBox>("SourceOverride")!.Text);
        }
    }
}
