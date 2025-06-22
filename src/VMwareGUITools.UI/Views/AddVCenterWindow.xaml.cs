using System.Windows;
using VMwareGUITools.UI.ViewModels;

namespace VMwareGUITools.UI.Views;

/// <summary>
/// Interaction logic for AddVCenterWindow.xaml
/// </summary>
public partial class AddVCenterWindow : Window
{
    public AddVCenterWindow(AddVCenterViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        
        // Set window owner and handle dialog result
        if (Application.Current.MainWindow != this)
        {
            Owner = Application.Current.MainWindow;
        }
        
        // Subscribe to view model events
        viewModel.DialogResultRequested += (result) => DialogResult = result;
    }
} 