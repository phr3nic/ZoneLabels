using System.Runtime.InteropServices;

namespace ZoneLab;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    private static void Main()
    {
        // WinExe has no console of its own. If we were launched from a terminal,
        // borrow the parent console so Console.Error actually lands somewhere.
        AttachConsole(ATTACH_PARENT_PROCESS);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => Log.Fatal(e.ExceptionObject as Exception);

        using var form = new MainForm();
        Application.Run(form);
    }
}