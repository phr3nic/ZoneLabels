using System.Text;

namespace ZoneLab;

/// <summary>
/// Everything goes to stderr and to dataset/capture.log. A failed capture must
/// never take the process down, so nothing in here throws.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string Directory { get; } =
        Path.Combine(AppContext.BaseDirectory, "dataset");

    public static string FilePath { get; } = Path.Combine(Directory, "capture.log");

    public static void Info(string message) => Write(Directory, "INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write(Directory, "ERR ", message, ex);

    public static void ErrorAt(string datasetDirectory, string message, Exception? ex = null) =>
        Write(datasetDirectory, "ERR ", message, ex);

    public static void Fatal(Exception? ex) => Write(Directory, "FATAL", "unhandled exception", ex);

    private static void Write(string datasetDirectory, string level, string message, Exception? ex)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
            .Append(' ').Append(level).Append(' ').Append(message)
            .Append(ex is null ? string.Empty : Environment.NewLine + ex)
            .ToString();

        try { Console.Error.WriteLine(line); } catch { }
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(datasetDirectory);
                File.AppendAllText(Path.Combine(datasetDirectory, "capture.log"), line + Environment.NewLine);
            }
        }
        catch { }
    }
}