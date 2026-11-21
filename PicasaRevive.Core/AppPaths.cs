namespace PicasaRevive.Core;

/// <summary>Resolves where the catalog, thumbnail cache and sample library live.</summary>
public sealed class AppPaths
{
    public const string DataDirEnvVar = "PICASA_REVIVE_DATA";

    public string DataDir { get; }
    public string DatabaseFile => Path.Combine(DataDir, "picasa_data.db");
    public string CacheDir => Path.Combine(DataDir, ".cache");
    public string ThumbDir => Path.Combine(CacheDir, "thumbnails");
    public string CollageDir => Path.Combine(CacheDir, "collages");
    public string SampleLibraryDir => Path.Combine(DataDir, "sample_library");
    public string WebViewDataDir => Path.Combine(DataDir, "WebView2");

    public AppPaths(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ThumbDir);
        Directory.CreateDirectory(CollageDir);
    }

    /// <summary>
    /// Order: PICASA_REVIVE_DATA env var; a "portable" marker file or a Debug build keeps data next to the exe;
    /// otherwise %LOCALAPPDATA%\PicasaRevive (the installed build).
    /// </summary>
    public static AppPaths Resolve(bool devBuild)
    {
        var env = Environment.GetEnvironmentVariable(DataDirEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return new AppPaths(env);

        var exeDir = AppContext.BaseDirectory;
        if (devBuild || File.Exists(Path.Combine(exeDir, "portable")) || File.Exists(Path.Combine(exeDir, "portable.txt")))
            return new AppPaths(Path.Combine(exeDir, "PicasaReviveData"));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, "PicasaRevive"));
    }

    public static string DefaultPicturesFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Path.Combine(home, "Pictures"),
            Path.Combine(home, "OneDrive", "Pictures"),
        };
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && Directory.Exists(c)) return c;
        return home;
    }
}
