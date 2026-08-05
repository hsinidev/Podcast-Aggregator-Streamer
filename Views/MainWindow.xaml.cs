using System.Windows;
using PodcastAggregatorStreamer.ViewModels;

namespace PodcastAggregatorStreamer.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnCloseAddFeedDialog(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.IsAddFeedDialogOpen = false;
            }
        }

        private void OnCloseOpmlDialog(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.IsOpmlDialogOpen = false;
            }
        }
    }
}
