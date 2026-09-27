using System.Windows;
namespace MakanDownloadManager;
public partial class HistoryWindow : Window
{
    public HistoryWindow(){InitializeComponent();Grid.ItemsSource=new Services.HistoryService(App.Db).Load();}
    void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
