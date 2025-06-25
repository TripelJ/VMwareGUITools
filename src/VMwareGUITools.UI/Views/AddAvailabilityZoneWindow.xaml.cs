using System.Windows;
using VMwareGUITools.UI.ViewModels;

namespace VMwareGUITools.UI.Views;

/// <summary>
/// Interaction logic for AddAvailabilityZoneWindow.xaml
/// </summary>
public partial class AddAvailabilityZoneWindow : Window
{
    public AddAvailabilityZoneWindow(AvailabilityZoneViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        
        // Handle view model commands
        if (viewModel != null)
        {
            viewModel.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(AvailabilityZoneViewModel.StatusMessage))
                {
                    // Check if zone was created successfully
                    if (viewModel.StatusMessage?.Contains("Created availability zone") == true)
                    {
                        DialogResult = true;
                        Close();
                    }
                }
            };
        }
    }
} 