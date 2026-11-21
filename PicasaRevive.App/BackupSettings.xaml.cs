using System.Windows;
using Microsoft.Win32;
using PicasaRevive.Core;

namespace PicasaRevive.App;

public partial class BackupSettings : Window
{
    private readonly AutomaticBackups _backups;
    private static readonly int[] MinutesPerUnit = { 1, 60, 1440 };

    public BackupSettings(AutomaticBackups backups)
    {
        InitializeComponent();
        _backups = backups;
        var schedule = backups.Schedule;
        EnabledBox.IsChecked = schedule.Enabled;
        UnitBox.SelectedIndex = schedule.Minutes % 1440 == 0 ? 2 : schedule.Minutes % 60 == 0 ? 1 : 0;
        IntervalBox.Text = (schedule.Minutes / MinutesPerUnit[UnitBox.SelectedIndex]).ToString();
        FolderBox.Text = schedule.Folder;
        LastRun.Text = backups.LastBackup is { } last
            ? $"Last successful backup: {last.Completed.ToLocalTime():g}\\n{last.File}"
            : "Waiting for the first automatic backup.";
        Problem.Text = backups.LastError ?? "";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a backup folder" };
        if (picker.ShowDialog(this) == true) FolderBox.Text = picker.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalBox.Text, out var value) || value < 1 || value > 525600 / MinutesPerUnit[UnitBox.SelectedIndex])
        {
            Problem.Text = "Enter a whole-number interval from one minute to 365 days.";
            return;
        }
        try
        {
            _backups.Configure(new(EnabledBox.IsChecked == true, value * MinutesPerUnit[UnitBox.SelectedIndex], FolderBox.Text.Trim()));
            DialogResult = true;
        }
        catch (Exception ex) { Problem.Text = ex.Message; }
    }
}
