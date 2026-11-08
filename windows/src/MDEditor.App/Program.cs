using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MDEditor;

/// <summary>Entry point: forwards to a running instance when there is one, otherwise starts the UI.</summary>
public static partial class Program
{
    /// <summary>Also named in installer/MDEditor.iss (AppMutex) so setup can detect a running app.</summary>
    private const string MutexName = "MDEditor.SingleInstance";

    private static string PipeName =>
        $"MDEditor-{WindowsIdentity.GetCurrent().User?.Value}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string id);

    [STAThread]
    private static int Main(string[] args)
    {
        string? smokeOutput = null;
        bool smoke = false;
        var files = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith("--smoke-test", StringComparison.OrdinalIgnoreCase))
            {
                smoke = true;
                int eq = arg.IndexOf('=');
                if (eq > 0) smokeOutput = arg[(eq + 1)..];
            }
            else if (arg.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(arg["--lang=".Length..]);
            }
            else if (arg.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
            {
                App.ForcedTheme = arg["--theme=".Length..].Equals("dark", StringComparison.OrdinalIgnoreCase) ? ApplicationTheme.Dark : ApplicationTheme.Light;
            }
            else if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                // Resolved here: a forwarded relative path would otherwise use the wrong working directory.
                try { files.Add(Path.GetFullPath(arg)); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { /* ignored */ }
            }
        }

        Mutex? mutex = null;
        if (!smoke)
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out bool primary);
            if (!primary)
            {
                ForwardToPrimary(files);
                mutex.Dispose();
                return 0;
            }
            Task.Run(ServePipe);
        }

        _ = SetCurrentProcessExplicitAppUserModelID("JYe16.MDEditor");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(start =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App(files, smoke, smokeOutput);
        });
        GC.KeepAlive(mutex);
        mutex?.Dispose();
        return 0;
    }

    private static void ForwardToPrimary(List<string> files)
    {
        const int attempts = 20;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(500);
                AllowSetForegroundWindow(-1);
                client.Write(JsonSerializer.SerializeToUtf8Bytes(files.ToArray(), ProgramJson.Default.StringArray));
                return;
            }
            catch (Exception e) when (e is TimeoutException or IOException)
            {
                Thread.Sleep(150); // The primary instance may still be starting.
            }
        }
    }

    private static async Task ServePipe()
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                using var buffer = new MemoryStream();
                await server.CopyToAsync(buffer);
                string[]? files = null;
                try { files = JsonSerializer.Deserialize(buffer.ToArray(), ProgramJson.Default.StringArray); }
                catch (JsonException) { /* not one of ours */ }
                if (files is not null) App.Instance?.Dispatcher.TryEnqueue(() => App.Instance.OpenFromOtherInstance(files));
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class ProgramJson : System.Text.Json.Serialization.JsonSerializerContext;
