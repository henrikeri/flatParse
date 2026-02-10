using System.Windows;
using FlatMaster.WPF.ViewModels;

namespace FlatMaster.WPF.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
