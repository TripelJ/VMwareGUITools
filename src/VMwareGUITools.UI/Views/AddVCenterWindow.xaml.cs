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
        
        // Set window owner
        if (Application.Current.MainWindow != this)
        {
            Owner = Application.Current.MainWindow;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        
        // Setup the view model after the window is properly initialized
        SetupViewModel();
    }
    
    private void SetupViewModel()
    {
        if (DataContext is AddVCenterViewModel viewModel)
        {
            // Subscribe to dialog result event now that window is initialized
            viewModel.DialogResultRequested += (result) => DialogResult = result;
        }
    }
} 