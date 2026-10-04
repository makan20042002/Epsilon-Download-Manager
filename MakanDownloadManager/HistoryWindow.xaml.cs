using System.Windows;
namespace MakanDownloadManager;
public partial class HistoryWindow : Window
{
    readonly Services.HistoryService _history = new(App.Db);
    public HistoryWindow(){InitializeComponent();Refresh();}
    void Refresh()=>Grid.ItemsSource=_history.Load();
    void Clear_Click(object sender,RoutedEventArgs e)
    {
        if (Dlg.Show(this,"Clear the complete download history?\n\nDownloaded files and the current download list are not deleted.","Download History",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK) return;
        _history.Clear(); Refresh();
    }
    void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
