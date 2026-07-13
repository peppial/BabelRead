using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using BabelRead.App.ViewModels;

namespace BabelRead.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
        : this(null)
    {
    }

    public SettingsWindow(SettingsViewModel? viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        if (viewModel is not null)
        {
            this.FindControl<ContentControl>("SettingsContent")!.Content = new SettingsView { DataContext = viewModel };
            Opened += async (_, _) => await viewModel.LoadAsync();
        }
    }
}
