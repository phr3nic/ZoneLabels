using System.Runtime.InteropServices;

namespace ZoneLab;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        // WinExe has no console of its own. If we were launched from a terminal,
        // borrow the parent console so Console.Error actually lands somewhere.
        AttachConsole(ATTACH_PARENT_PROCESS);

        if (args.Length > 0 && string.Equals(args[0], "--validate-manifest", StringComparison.OrdinalIgnoreCase))
        {
            string datasetRoot = args.Length > 1 ? args[1] : Log.Directory;
            try
            {
                var store = new DatasetManifestStore(datasetRoot);
                DatasetManifest manifest = store.LoadOrMigrate();
                ManifestValidationResult result = store.ValidateAgainstFiles();
                Console.WriteLine($"Manifest version: {manifest.Version}");
                Console.WriteLine($"Captures: {manifest.Captures.Count}");
                Console.WriteLine($"Missing files (manifest -> disk): {result.MissingFiles.Count}");
                foreach (string file in result.MissingFiles)
                    Console.WriteLine($"  {file}");
                Console.WriteLine($"Orphan images (disk -> manifest): {result.OrphanImages.Count}");
                foreach (string file in result.OrphanImages)
                    Console.WriteLine($"  {file}");
                Console.WriteLine(result.MissingFiles.Count == 0 && result.OrphanImages.Count == 0
                    ? "Manifest validation: CLEAN"
                    : "Manifest validation: ISSUES FOUND");
                return result.MissingFiles.Count == 0 && result.OrphanImages.Count == 0 ? 0 : 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            {
                Log.ErrorAt(datasetRoot, $"manifest validation failed: {datasetRoot}", ex);
                Console.Error.WriteLine($"Manifest validation failed: {ex.Message}");
                return 2;
            }
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => Log.Fatal(e.ExceptionObject as Exception);

        using var form = new MainForm();
        Application.Run(form);
        return 0;
    }
}