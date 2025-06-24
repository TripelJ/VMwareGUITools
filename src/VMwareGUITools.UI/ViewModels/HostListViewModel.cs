using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VMwareGUITools.Core.Models;

namespace VMwareGUITools.UI.ViewModels
{
    /// <summary>
    /// ViewModel for managing the list of VMware hosts
    /// </summary>
    public class HostListViewModel : INotifyPropertyChanged
    {
        private ObservableCollection<Host> _hosts;
        private Host? _selectedHost;
        private bool _isLoading;

        public HostListViewModel()
        {
            _hosts = new ObservableCollection<Host>();
        }

        /// <summary>
        /// Collection of hosts
        /// </summary>
        public ObservableCollection<Host> Hosts
        {
            get => _hosts;
            set
            {
                _hosts = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Currently selected host
        /// </summary>
        public Host? SelectedHost
        {
            get => _selectedHost;
            set
            {
                _selectedHost = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Indicates if data is being loaded
        /// </summary>
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                _isLoading = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Refreshes the host list
        /// </summary>
        public async Task RefreshAsync()
        {
            // TODO: Implement host refresh logic
            IsLoading = true;
            try
            {
                // Implementation will be added when host data access is available
                await Task.Delay(100); // Placeholder
            }
            finally
            {
                IsLoading = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
} 