using System.Windows;
using Microsoft.Win32;
using PicasaRevive.Core;
using PicasaRevive.Server;

namespace PicasaRevive.App;

/// <summary>
/// Application entry: enforces a single instance, starts the loopback backend, opens the main window,
/// and owns the one shutdown path every exit route funnels into.
/// </summary>
public partial class App : Application
{
    // One running instance per library folder: the names get a hash of the data directory appended.
    private string _mutexName = "PicasaRevive.SingleInstance.v2";
    private string _activateEventName = "PicasaRevive.Activate.v2";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private PicasaServer? _server;
    private readonly CancellationTokenSource _stopBackups = new();
    private Task? _backupWorker;
    private int _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        const bool devBuild = true;
#else
        const bool devBuild = false;
#endif
        AppPaths paths;
        try
        {
            paths = AppPaths.Resolve(devBuild);
        }
        catch (Exception ex)
        {
            MessageBox.Show("svacitra could not open its data folder: " + ex.Message, "svacitra", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(paths.DataDir.ToLowerInvariant())), 0, 6);
        _mutexName += "." + id;
        _activateEventName += "." + id;

        _mutex = new Mutex(initiallyOwned: true, _mutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Another copy is running: ask it to come to the front, then leave.
            try
            {
                using var existing = EventWaitHandle.OpenExisting(_activateEventName);
                existing.Set();
            }
            catch (Exception) { }
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _activateEventName);
        var listener = new Thread(() =>
        {
            try
            {
                while (_activateEvent.WaitOne())
                {
                    if (Volatile.Read(ref _exiting) == 1) break;
                    Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront());
                }
            }
            catch (ObjectDisposedException) { }
        })
        { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "svacitra - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            _server = await PicasaServer.StartAsync(new ServerOptions
            {
                Paths = paths,
                RequireAuth = true,
                OnShutdownRequested = () => Dispatcher.BeginInvoke(() => _ = ExitAsync()),
                PickFolder = async () =>
                {
                    var picked = await Dispatcher.InvokeAsync(() => PickFolder("Choose a photo folder"));
                    if (picked is not null) _server?.ApproveExportFolder(picked);
                    return picked;
                },
                PickVideoDestination = async count => await Dispatcher.InvokeAsync(() =>
                {
                    var length = TimeSpan.FromMilliseconds((long)count * SlideshowVideo.SlideMilliseconds);
                    var save = new SaveFileDialog { Title = $"Export {count} slides ({length:g})", Filter = "MP4 video (*.mp4)|*.mp4", FileName = "Slideshow.mp4", DefaultExt = ".mp4" };
                    return save.ShowDialog(MainWindow) == true ? save.FileName : null;
                }),
                PickCollageDestination = async () => await Dispatcher.InvokeAsync(() =>
                {
                    var save = new SaveFileDialog { Filter = "JPEG image (*.jpg)|*.jpg", FileName = "Collage.jpg", DefaultExt = ".jpg" };
                    return save.ShowDialog(MainWindow) == true ? save.FileName : null;
                }),
                RescanOnStart = true,
            });

            var backups = new AutomaticBackups(_server.Catalog, paths);
            _backupWorker = Task.Run(() => backups.RunAsync(_stopBackups.Token));
            var window = new MainWindow(_server, backups);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("svacitra could not start:\n\n" + ex.Message, "svacitra", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    /// <summary>Native folder picker shared by the menu and the frontend's "Browse..." button.</summary>
    public static string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        var owner = Current?.MainWindow;
        bool? ok = owner is { IsVisible: true } ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// The single exit path (menu, Ctrl+Q, Alt+F4, title-bar X, top-bar Exit button, /api/shutdown):
    /// stop Kestrel, shut WPF down, then end the process so nothing is left in Task Manager.
    /// </summary>
    public async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1) return;

        // Failsafe: whatever happens below, the process is gone within a few seconds.
        var watchdog = new Thread(() => { Thread.Sleep(5000); Environment.Exit(0); }) { IsBackground = true, Name = "ExitWatchdog" };
        watchdog.Start();

        try { (MainWindow as MainWindow)?.PrepareForExit(); } catch (Exception) { }
        _stopBackups.Cancel();
        try { if (_backupWorker is not null) await _backupWorker; } catch (Exception) { }
        try { if (_server is not null) await _server.StopAsync(); } catch (Exception) { }
        try { _activateEvent?.Set(); _activateEvent?.Dispose(); } catch (Exception) { }
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch (Exception) { }

        Shutdown(0);
        Environment.Exit(0);
    }
}
