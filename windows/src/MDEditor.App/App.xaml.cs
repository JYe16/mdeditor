using MDEditor.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MDEditor;

public partial class App : Application
{
    internal static App? Instance { get; private set; }

    /// <summary>--theme=dark|light (diagnostic): overrides following the system theme.</summary>
    internal static ApplicationTheme? ForcedTheme { get; set; }

    internal DispatcherQueue Dispatcher { get; private set; } = null!;
    internal AppSettings Settings { get; }
    internal bool IsSmokeTest { get; }

    internal static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDEditor");

    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    private readonly List<string> _startupFiles;
    private readonly string? _smokeOutput;
    private readonly List<MainWindow> _windows = new();

    internal App(List<string> files, bool smoke, string? smokeOutput)
    {
        Instance = this;
        _startupFiles = files;
        IsSmokeTest = smoke;
        _smokeOutput = smokeOutput;
        Settings = smoke ? new AppSettings() : AppSettings.Load(SettingsPath);

        // An unpackaged app's default WebView2 data folder sits next to the exe, which may be read-only.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(DataDirectory, "WebView2"));
        if (ForcedTheme is { } theme) RequestedTheme = theme;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.AppendAllText(Path.Combine(DataDirectory, "crash.log"), $"{DateTime.Now:O}\n{e.Exception}\n\n");
            }
            catch (IOException) { /* nothing more can be done */ }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Dispatcher = DispatcherQueue.GetForCurrentThread();
        if (IsSmokeTest)
        {
            var window = NewWindow();
            _ = new SmokeTest(window, _smokeOutput).RunAsync();
            return;
        }

        if (_startupFiles.Count == 0)
        {
            NewWindow();
            return;
        }
        foreach (string file in _startupFiles) OpenFile(file);
        if (_windows.Count == 0) NewWindow();
    }

    internal MainWindow NewWindow()
    {
        var window = new MainWindow();
        _windows.Add(window);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            SaveSettings();
        };
        window.Activate();
        return window;
    }

    /// <summary>Focuses the window already showing the file, reuses a pristine Untitled one, or opens a new one.</summary>
    internal void OpenFile(string path, MainWindow? origin = null)
    {
        var existing = _windows.FirstOrDefault(w => w.IsShowing(path));
        if (existing is not null)
        {
            existing.BringToFront();
            return;
        }

        if (origin is { IsPristine: true })
        {
            _ = origin.LoadFileAsync(path);
            return;
        }

        var pristine = _windows.FirstOrDefault(w => w.IsPristine);
        var target = pristine ?? NewWindow();
        _ = target.LoadFileAsync(path);
        target.BringToFront();
    }

    internal void OpenFromOtherInstance(string[] files)
    {
        if (files.Length == 0)
        {
            NewWindow().BringToFront();
            return;
        }
        foreach (string file in files) OpenFile(file);
    }

    internal void SaveSettings()
    {
        if (!IsSmokeTest) Settings.Save(SettingsPath);
    }

    /// <summary>Applies a view mode or zoom change made in one window to every window.</summary>
    internal void BroadcastSettingsChanged()
    {
        foreach (var window in _windows) window.ApplySharedSettings();
        SaveSettings();
    }

    internal void RequestExit()
    {
        foreach (var window in _windows.ToArray()) window.RequestClose();
    }

    /// <summary>Ends the smoke test. Application.Exit crashes in CoreMessagingXP at teardown, which would hide the result.</summary>
    internal static void Finish(int exitCode) => Environment.Exit(exitCode);
}
