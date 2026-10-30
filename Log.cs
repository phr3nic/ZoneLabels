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

    public static void Error(string message, Exception? ex = null) => Write("ERR ", message, ex);

    public static void Fatal(Exception? ex) => Write("FATAL", "unhandled exception", ex);

    private static void Write(string level, string message, Exception? ex)
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
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch { }
    }
}