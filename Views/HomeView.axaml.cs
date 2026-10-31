using Avalonia.Controls;
using Avalonia.Interactivity;
using MiHome.ViewModels;

namespace MiHome.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void OnGotoScenes(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SelectedNavIndex = 2;
    }

    private void OnGotoRooms(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SelectedNavIndex = 1;
    }
}
