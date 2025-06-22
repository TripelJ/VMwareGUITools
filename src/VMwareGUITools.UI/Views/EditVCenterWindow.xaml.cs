using System.Windows;
using VMwareGUITools.UI.ViewModels;

namespace VMwareGUITools.UI.Views;

/// <summary>
/// Interaction logic for EditVCenterWindow.xaml
/// </summary>
public partial class EditVCenterWindow : Window
{
    public EditVCenterWindow()
    {
        InitializeComponent();
        
        // Set window owner and handle dialog result
        if (Application.Current.MainWindow != this)
        {
            Owner = Application.Current.MainWindow;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        
        // Subscribe to view model events when DataContext is set
        if (DataContext is EditVCenterViewModel viewModel)
        {
            viewModel.DialogResultRequested += (result) => DialogResult = result;
        }
    }
} 