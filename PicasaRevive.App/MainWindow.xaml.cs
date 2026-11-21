using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using PicasaRevive.Server;

namespace PicasaRevive.App;

/// <summary>Native shell: classic menu bar on top of a WebView2 that shows the embedded frontend.</summary>
public partial class MainWindow : Window
{
    private readonly PicasaServer _server;
    private bool _webReady;
    private bool _fullscreen;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResize;

    private readonly PicasaRevive.Core.AutomaticBackups _backups;

    public MainWindow(PicasaServer server, PicasaRevive.Core.AutomaticBackups backups)
    {
        _server = server;
        _backups = backups;
        InitializeComponent();
        Loaded += async (_, _) => await InitializeWebViewAsync();
        PreviewKeyDown += OnPreviewKeyDown;
        Activated += (_, _) => RestoreWebFocus();
        MainMenu.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler((_, _) => RestoreWebFocus()));
    }

    // ------------------------------------------------------------------ dark title bar

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE
            int caption = 0x00211D1B;                                      // COLORREF (BGR) of #1B1D21, Windows 11 only
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));     // DWMWA_CAPTION_COLOR
        }
        catch (Exception) { /* cosmetic only; older Windows keeps the default title bar */ }
    }

    // ------------------------------------------------------------------ WebView2

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _server.Paths.WebViewDataDir);
            await Browser.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            // WebView2 Evergreen runtime missing or broken (it ships with Windows 11 and current Windows 10).
            Browser.Visibility = Visibility.Collapsed;
            FallbackPanel.Visibility = Visibility.Visible;
            FallbackText.Text = "The app's interface is rendered with WebView2, which is preinstalled on Windows 11 and up-to-date " +
                                "Windows 10. It could not be started on this PC.\n\n" + ex.Message +
                                "\n\nInstall the free Evergreen runtime from Microsoft, or keep using svacitra in your web browser " +
                                "(this window must stay open).";
            return;
        }

        var core = Browser.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false; // no F5 / Ctrl+R reloads; app shortcuts still reach the page
        s.IsStatusBarEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.AreDefaultScriptDialogsEnabled = false;   // alert()/confirm() become native message boxes
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif

        await core.AddScriptToExecuteOnDocumentCreatedAsync("window.__PICASA_NATIVE__ = true;");

        core.ScriptDialogOpening += OnScriptDialogOpening;
        core.WindowCloseRequested += (_, _) => Close();
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            // The window only ever shows our own loopback page; anything else opens in the real browser.
            if (!e.Uri.StartsWith(_server.BaseUrl, StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                Dispatcher.BeginInvoke(() => { try { core.Reload(); } catch (Exception) { } });
        };

        var cookie = core.CookieManager.CreateCookie(PicasaServer.SessionCookieName, _server.SessionToken, "127.0.0.1", "/");
        cookie.IsHttpOnly = true;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        core.CookieManager.AddOrUpdateCookie(cookie);

        _webReady = true;
        core.Navigate(_server.BaseUrl + "/");
        Browser.Focus();
    }

    /// <summary>Shows page alert()/confirm() calls as proper "svacitra" message boxes.</summary>
    private void OnScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        var deferral = e.GetDeferral();
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                switch (e.Kind)
                {
                    case CoreWebView2ScriptDialogKind.Confirm:
                        if (MessageBox.Show(this, e.Message, "svacitra", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                            e.Accept();
                        break;
                    case CoreWebView2ScriptDialogKind.Beforeunload:
                        e.Accept();
                        break;
                    default:
                        MessageBox.Show(this, e.Message, "svacitra", MessageBoxButton.OK, MessageBoxImage.Information);
                        e.Accept();
                        break;
                }
            }
            finally
            {
                deferral.Complete();
                RestoreWebFocus();
            }
        });
    }

    /// <summary>
    /// Gives keyboard focus back to the web view. After a native dialog or a menu command WPF still believes
    /// the WebView2 control is focused, so a plain Focus() is a no-op and typing in the page goes nowhere.
    /// Clearing focus first forces the control to hand real input focus to the browser again.
    /// </summary>
    private void RestoreWebFocus()
    {
        if (!_webReady) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (!_webReady || !IsActive || MainMenu.IsKeyboardFocusWithin) return;
            FocusManager.SetFocusedElement(this, null);
            Keyboard.ClearFocus();
            Browser.Focus();
        });
    }

    private async Task<string?> RunScriptAsync(string script)
    {
        if (!_webReady) return null;
        try { return await Browser.CoreWebView2.ExecuteScriptAsync(script); }
        catch (Exception) { return null; }
    }

    private static string JsString(string value) => JsonSerializer.Serialize(value);

    private static void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ window lifetime

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Called by <see cref="App.ExitAsync"/>: releases the browser so its child processes end too.</summary>
    public void PrepareForExit()
    {
        _webReady = false;
        Hide();
        try { Browser.Dispose(); } catch (Exception) { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Title-bar X and Alt+F4 land here. Hand over to the single exit path.
        e.Cancel = true;
        _ = ((App)Application.Current).ExitAsync();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _fullscreen) { ToggleFullscreen(); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { Zoom(+0.1); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { Zoom(-0.1); e.Handled = true; }
        else if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0)) { Zoom(0); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { AddFolder_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreResize = ResizeMode;
            MainMenu.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal; // required so Maximized covers the taskbar once the border is gone
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _fullscreen = true;
        }
        else
        {
            MainMenu.Visibility = Visibility.Visible;
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            WindowState = _restoreState;
            _fullscreen = false;
        }
        FullscreenItem.IsChecked = _fullscreen;
    }

    private void Zoom(double delta)
    {
        if (!_webReady) return;
        Browser.ZoomFactor = delta == 0 ? 1.0 : Math.Clamp(Browser.ZoomFactor + delta, 0.5, 3.0);
    }

    // ------------------------------------------------------------------ menu handlers

    /// <summary>Generic handler: the MenuItem's Tag holds the JavaScript to run in the page.</summary>
    private async void Script_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string script }) await RunScriptAsync(script);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = App.PickFolder("Add a photo folder to your svacitra library");
        if (folder is null) return;
        await RunScriptAsync($"PicasaNative.addFolder({JsString(folder)});");
    }

    private async void Export_Click(object sender, RoutedEventArgs e) => await RunScriptAsync("PicasaNative.exportSelected(null);");

    private async void ExportTo_Click(object sender, RoutedEventArgs e)
    {
        var folder = App.PickFolder("Export selected photos to...");
        if (folder is null) return;
        _server.ApproveExportFolder(folder);
        await RunScriptAsync($"PicasaNative.exportSelected({JsString(folder)});");
    }

    private const string BackupFilter = "svacitra library (*.prlibrary)|*.prlibrary";

    private void BackupSettings_Click(object sender, RoutedEventArgs e) => new BackupSettings(_backups) { Owner = this }.ShowDialog();

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = BackupFilter, FileName = $"svacitra Library {DateTime.Now:yyyy-MM-dd}", DefaultExt = ".prlibrary" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await Task.Run(() => _server.Catalog.BackUpLibrary(dialog.FileName));
            MessageBox.Show(this, $"Library backup saved to\n{dialog.FileName}", "Back Up Library");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Backup failed"); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = BackupFilter };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var backup = await Task.Run(() => PicasaRevive.Core.LibraryBackup.Read(dialog.FileName));
            if (MessageBox.Show(this, $"Merge {backup.Photos.Count} photo records into this library? Matching photos will use the edits, tags and stars from the backup.", "Restore Library", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var old in backup.Folders.Where(path => !System.IO.Directory.Exists(path)))
            {
                var answer = MessageBox.Show(this, $"Locate the new location for this folder?\n\n{old}\n\nChoose No to keep it offline.", "Reconnect Folder", MessageBoxButton.YesNoCancel);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.No) continue;
                var selected = App.PickFolder($"Reconnect {old}");
                if (selected is null) return;
                locations[old] = selected;
            }
            await Task.Run(() =>
            {
                var restored = _server.Catalog.RestoreLibrary(backup, locations);
                foreach (var path in restored) _server.Images.InvalidateEditedThumbnail(path);
            });
            _server.Scanner.RescanAll();
            await RunScriptAsync("PicasaLibrary.refresh()");
            MessageBox.Show(this, $"Restored {backup.Photos.Count} photo records.", "Restore Library");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Restore failed"); }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenExternal(_server.Paths.DataDir);

    private void Exit_Click(object sender, RoutedEventArgs e) => _ = ((App)Application.Current).ExitAsync();

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(+0.1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(-0.1);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => Zoom(0);

    private void Contact_Click(object sender, RoutedEventArgs e) => OpenExternal("mailto:fajari@gmail.com");

    private static string ReadNotice(string resourceName)
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Missing application notice: " + resourceName);
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void License_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        ReadNotice("svacitra.License"), "svacitra — MIT License", MessageBoxButton.OK, MessageBoxImage.Information);

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3);
        MessageBox.Show(this, $"Version {version}\n\n" + ReadNotice("svacitra.Disclaimer") +
            $"\nLibrary data: {_server.Paths.DataDir}", "About svacitra", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DownloadWebView2_Click(object sender, RoutedEventArgs e) => OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");
    private void OpenInBrowser_Click(object sender, RoutedEventArgs e) => OpenExternal($"{_server.BaseUrl}/?auth={_server.SessionToken}");
}
