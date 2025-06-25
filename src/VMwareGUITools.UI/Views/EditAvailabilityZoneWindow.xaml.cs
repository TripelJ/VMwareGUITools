using System.Windows;
using VMwareGUITools.UI.ViewModels;

namespace VMwareGUITools.UI.Views;

/// <summary>
/// Interaction logic for EditAvailabilityZoneWindow.xaml
/// </summary>
public partial class EditAvailabilityZoneWindow : Window
{
    public EditAvailabilityZoneWindow(EditAvailabilityZoneViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        
        // Subscribe to dialog result events
        viewModel.DialogResultRequested += (result) => DialogResult = result;
        
        // Handle status message changes to show validation feedback
        viewModel.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(EditAvailabilityZoneViewModel.StatusMessage))
            {
                // Additional UI feedback can be added here if needed
            }
        };
    }
} 