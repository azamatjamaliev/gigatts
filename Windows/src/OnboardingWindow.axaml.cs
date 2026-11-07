using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GigaTTS;

public partial class OnboardingWindow : Window
{
    readonly AppState _state;

    public OnboardingWindow(AppState state)
    {
        _state = state;
        DataContext = state;
        InitializeComponent();
        Icon = App.SharedIcon;
        Opened += (_, _) => _state.RefreshPermissions();
    }

    void OnContinue(object? sender, RoutedEventArgs e)
    {
        _state.FinishOnboarding();
        Close();
    }
}
