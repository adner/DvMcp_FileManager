using System.Globalization;

namespace DataverseFileManager.Explorer;

/// <summary>
/// Minimal thread-safe logger → console + a rolling file. cfapi callbacks fire on background
/// platform threads, so every line carries a timestamp and managed thread id to make the
/// interleaving legible. <see cref="FilePath"/> survives a crash (the writer auto-flushes).
/// </summary>
internal static class Log
{
    private static readonly Lock Gate = new();
    private static StreamWriter? _file;

    public static string? FilePath { get; private set; }

    public static void Init()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataverseFileManager");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "explorer.log");
            _file = new StreamWriter(FilePath, append: true) { AutoFlush = true };
            Write("INF", $"=== session start (pid {Environment.ProcessId}) ===");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Log file unavailable ({ex.Message}); console only.");
        }
    }

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex}");

    private static void Write(string level, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss.fff} [{level}] (t{Environment.CurrentManagedThreadId,3}) {message}");
        lock (Gate)
        {
            Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Flush();
            _file?.Dispose();
            _file = null;
        }
    }
}
