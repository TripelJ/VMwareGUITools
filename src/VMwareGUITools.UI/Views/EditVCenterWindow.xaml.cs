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
        
        // Subscribe to DataContext changes
        DataContextChanged += EditVCenterWindow_DataContextChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        
        // Initial setup if DataContext is already set
        SetupViewModel();
    }
    
    private void EditVCenterWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SetupViewModel();
    }
    
    private void SetupViewModel()
    {
        if (DataContext is EditVCenterViewModel viewModel)
        {
            viewModel.DialogResultRequested += (result) => DialogResult = result;
            
            // Handle password binding manually due to security restrictions
            PasswordBox.PasswordChanged += (sender, args) => 
            {
                viewModel.Password = PasswordBox.Password;
            };
            
            // Set initial password value
            PasswordBox.Password = viewModel.Password;
        }
    }
} 