using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MeetingScribe.Diagnostics;
using MeetingScribe.Infrastructure;

namespace MeetingScribe;

[SupportedOSPlatform("windows")]
internal static partial class Program
{
    private const int AttachParentProcess = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        HookGlobalExceptionHandlers();

        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
        {
            AttachConsole(AttachParentProcess);
            return SelfTest.RunAsync(args).GetAwaiter().GetResult();
        }

        using var singleInstance = new Mutex(
            initiallyOwned: true,
            @"Local\MeetingScribe.SingleInstance",
            out var isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "MeetingScribe is already running — look for the icon in your system tray.",
                "MeetingScribe",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
        return 0;
    }

    private static void HookGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
