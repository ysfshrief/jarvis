namespace Jarvis.Core;

/// <summary>
/// Every on-disk location JARVIS uses. All user data lives under one local folder
/// (%LOCALAPPDATA%\JARVIS on Windows) so it is easy to inspect, back up, or wipe.
/// Set the JARVIS_DATA_DIR environment variable to relocate it (used by tests).
/// </summary>
public sealed class JarvisPaths
{
    public JarvisPaths(string? dataDir = null)
    {
        DataDir = dataDir
            ?? Environment.GetEnvironmentVariable("JARVIS_DATA_DIR")
            ?? DefaultDataDir();
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ModelsDir);
    }

    public string DataDir { get; }
    public string DatabasePath => Path.Combine(DataDir, "jarvis.db");
    public string SettingsPath => Path.Combine(DataDir, "settings.json");
    public string SecretsPath => Path.Combine(DataDir, "secrets.dat");
    public string RuntimeInfoPath => Path.Combine(DataDir, "runtime.json");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string ModelsDir => Path.Combine(DataDir, "models");
    public string TrashDir => Path.Combine(DataDir, "trash");

    public static string ScreenshotsDir
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pictures))
                pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Path.Combine(pictures, "JARVIS");
        }
    }

    private static string DefaultDataDir()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JARVIS");

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Path.Combine(root, "jarvis");
    }
}
