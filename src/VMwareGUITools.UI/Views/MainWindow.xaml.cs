using System.Windows;
using VMwareGUITools.UI.ViewModels;

namespace VMwareGUITools.UI.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    // Default constructor for XAML instantiation
    public MainWindow()
    {
        InitializeComponent();
        // DataContext will be set later when created through DI
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
} 