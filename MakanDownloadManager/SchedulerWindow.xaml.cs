using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>IDM's Scheduler: several queues, each with its own start / stop times and "when done" actions.</summary>
public partial class SchedulerWindow : Window
{
    bool _loading;
    int _currentId;

    public SchedulerWindow(int? selectQueueId = null)
    {
        InitializeComponent();
        RefreshList(selectQueueId ?? App.Queues.Main.Id);
        App.Queues.Changed += OnQueuesChanged;
        Closed += (_, _) => App.Queues.Changed -= OnQueuesChanged;
    }

    // Queue events arrive while the user may be typing: refresh the captions and file list but never overwrite the form.
    void OnQueuesChanged() => Dispatcher.BeginInvoke(() => { RefreshList(_currentId, reload: false); LoadFiles(); UpdateRunning(); });

    CheckBox[] DayBoxes => new[] { DaySun, DayMon, DayTue, DayWed, DayThu, DayFri, DaySat };   // index = DayOfWeek

    // ---------------------------------------------------------------- left: queue list

    void RefreshList(int selectId, bool reload = true)
    {
        _loading = true;
        QueueList.Items.Clear();
        ListBoxItem? select = null;
        foreach (var q in App.Queues.Queues)
        {
            var item = new ListBoxItem { Content = (App.Queues.IsRunning(q.Id) ? "▶ " : "") + q.Name, Tag = q.Id };
            QueueList.Items.Add(item);
            if (q.Id == selectId) select = item;
        }
        QueueList.SelectedItem = select ?? QueueList.Items[0];
        _loading = false;
        if (QueueList.SelectedItem is ListBoxItem { Tag: int id }) _currentId = id;
        if (reload) LoadQueue();
    }

    void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QueueList.SelectedItem is not ListBoxItem { Tag: int id }) return;
        _currentId = id;
        LoadQueue();
    }

    void NewQueue_Click(object sender, RoutedEventArgs e)
    {
        var queue = App.Queues.AddQueue("New queue");
        RefreshList(queue.Id);
        NameBox.Focus(); NameBox.SelectAll();   // type the real name, then press Apply
    }

    void DeleteQueue_Click(object sender, RoutedEventArgs e)
    {
        var queue = App.Queues.Find(_currentId);
        if (queue == null || queue.IsMain) return;
        if (Dlg.Show(this, Loc.F("Delete the queue \"{0}\"?\nIts downloads stay in the list (stopped).", queue.Name), "Scheduler", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        App.Queues.RemoveQueue(queue.Id);
        RefreshList(App.Queues.Main.Id);
    }

    // ---------------------------------------------------------------- right: the selected queue

    void LoadQueue()
    {
        var queue = App.Queues.Find(_currentId);
        if (queue == null) return;
        var s = queue.Schedule;
        _loading = true;
        NameBox.Text = queue.Name; NameBox.IsEnabled = !queue.IsMain;
        DeleteQueueButton.IsEnabled = !queue.IsMain;
        StartOnStartup.IsChecked = s.StartOnStartup;
        StartAt.IsChecked = s.StartAtEnabled; StartTime.Text = s.StartTime;
        OnceRadio.IsChecked = !s.Daily; DailyRadio.IsChecked = s.Daily;
        OnceDate.SelectedDate = DateTime.TryParseExact(s.OnceDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var once) ? once : DateTime.Today;
        for (var i = 0; i < 7; i++) DayBoxes[i].IsChecked = ((s.Days >> i) & 1) == 1;
        StopAt.IsChecked = s.StopAtEnabled; StopTime.Text = s.StopTime;
        ParallelBox.Text = queue.MaxParallel.ToString(CultureInfo.InvariantCulture);
        RetriesOn.IsChecked = s.RetriesEnabled; RetriesBox.Text = s.Retries.ToString(CultureInfo.InvariantCulture);
        OpenOn.IsChecked = s.OpenFileEnabled; OpenPath.Text = s.OpenFile ?? "";
        ExitOn.IsChecked = s.ExitWhenDone;
        PowerOn.IsChecked = s.PowerOffWhenDone; PowerBox.SelectedIndex = (int)s.PowerAction; ForceOn.IsChecked = s.ForcePowerOff;
        _loading = false;
        LoadFiles();
        UpdateRunning();
    }

    void LoadFiles() => FilesList.ItemsSource = App.Queues.ItemsOf(_currentId);

    void UpdateRunning()
    {
        var running = App.Queues.IsRunning(_currentId);
        RunningText.Text = running ? "Running" : "Not running";
        StartNowButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
    }

    static bool TryTime(string text, out string normalized)
    {
        normalized = "";
        if (!TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1)) return false;
        normalized = $"{t.Hours:00}:{t.Minutes:00}";
        return true;
    }

    /// <summary>Reads the form into the queue. Returns false (after telling the user) when something is not valid.</summary>
    bool Apply()
    {
        var queue = App.Queues.Find(_currentId);
        if (queue == null) return false;
        var s = queue.Schedule.Clone();
        if (!TryTime(StartTime.Text, out var start)) { Warn("Start time must look like 23:30 (24-hour)."); return false; }
        if (!TryTime(StopTime.Text, out var stop)) { Warn("Stop time must look like 07:30 (24-hour)."); return false; }
        if (!int.TryParse(RetriesBox.Text.Trim(), out var retries) || retries < 0 || retries > 100) { Warn("The number of retries must be between 0 and 100."); return false; }
        if (!int.TryParse(ParallelBox.Text.Trim(), out var parallel) || parallel < 1 || parallel > 16) { Warn("The number of files at the same time must be between 1 and 16."); return false; }
        var days = 0;
        for (var i = 0; i < 7; i++) if (DayBoxes[i].IsChecked == true) days |= 1 << i;
        var daily = DailyRadio.IsChecked == true;
        if (StartAt.IsChecked == true && daily && days == 0) { Warn("Tick at least one day."); return false; }
        if (StartAt.IsChecked == true && !daily && OnceDate.SelectedDate == null) { Warn("Choose the date."); return false; }

        s.StartOnStartup = StartOnStartup.IsChecked == true;
        s.StartAtEnabled = StartAt.IsChecked == true; s.StartTime = start; s.Daily = daily; s.Days = days;
        s.OnceDate = OnceDate.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        s.LastStartedOn = null;   // a changed schedule may fire again today
        s.StopAtEnabled = StopAt.IsChecked == true; s.StopTime = stop;
        s.RetriesEnabled = RetriesOn.IsChecked == true; s.Retries = retries;
        s.OpenFileEnabled = OpenOn.IsChecked == true; s.OpenFile = string.IsNullOrWhiteSpace(OpenPath.Text) ? null : OpenPath.Text.Trim();
        s.ExitWhenDone = ExitOn.IsChecked == true;
        s.PowerOffWhenDone = PowerOn.IsChecked == true; s.PowerAction = (PowerAction)Math.Max(0, PowerBox.SelectedIndex); s.ForcePowerOff = ForceOn.IsChecked == true;

        App.Queues.SetMaxParallel(queue.Id, parallel);
        App.Queues.UpdateSchedule(queue.Id, s);
        if (!queue.IsMain && NameBox.Text.Trim().Length > 0 && NameBox.Text.Trim() != queue.Name) App.Queues.RenameQueue(queue.Id, NameBox.Text);
        return true;
    }

    void Warn(string text) => Dlg.Show(this, text, "Scheduler", MessageBoxButton.OK, MessageBoxImage.Warning);

    void ChangeParallel(int delta)
    {
        var n = int.TryParse(ParallelBox.Text.Trim(), out var current) ? current : 4;
        ParallelBox.Text = Math.Clamp(n + delta, 1, 16).ToString(CultureInfo.InvariantCulture);
    }
    void ParallelUp_Click(object sender, RoutedEventArgs e) => ChangeParallel(+1);
    void ParallelDown_Click(object sender, RoutedEventArgs e) => ChangeParallel(-1);

    void Apply_Click(object sender, RoutedEventArgs e) { if (Apply()) RefreshList(_currentId); }
    void StartNow_Click(object sender, RoutedEventArgs e) { if (!Apply()) return; App.Queues.Start(_currentId); RefreshList(_currentId); UpdateRunning(); }
    void Stop_Click(object sender, RoutedEventArgs e) { App.Queues.Stop(_currentId); RefreshList(_currentId); UpdateRunning(); }
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void BrowseOpen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "File to open when the queue is finished" };
        if (dialog.ShowDialog(this) == true) OpenPath.Text = dialog.FileName;
    }

    // ---------------------------------------------------------------- files in the queue

    void MoveSelected(int delta)
    {
        var selected = FilesList.SelectedItems.OfType<Models.DownloadItem>().ToList();
        if (selected.Count == 0) return;
        foreach (var item in delta < 0 ? selected : Enumerable.Reverse(selected)) App.Queues.MoveItem(_currentId, item.Id, delta);
        LoadFiles();
    }
    void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);
    void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in FilesList.SelectedItems.OfType<Models.DownloadItem>().ToList()) App.Queues.RemoveItem(item);
        LoadFiles();
    }
}
