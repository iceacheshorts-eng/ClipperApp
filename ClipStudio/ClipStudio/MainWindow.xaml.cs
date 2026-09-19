using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace ClipStudio;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
    using ClipStudio.ViewModels;
    using ClipStudio.Services;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var logger = new ActivityLogger();
        DataContext = new MainViewModel(logger);
        
        var notifyCollection = ActivityLogList.Items as System.Collections.Specialized.INotifyCollectionChanged;
        if (notifyCollection != null)
        {
            notifyCollection.CollectionChanged += (s, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems != null && e.NewItems.Count > 0)
                {
                    var item = e.NewItems[0];
                    if (item != null)
                    {
                        ActivityLogList.ScrollIntoView(item);
                    }
                }
            };
        }
    }
}