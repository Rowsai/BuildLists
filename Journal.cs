using Dalamud.Plugin.Services;

namespace BuildLists;
public sealed record LogEntry(DateTimeOffset Time, string Level, string Message);
public sealed class Journal(IPluginLog sink, string directory)
{
    private readonly Queue<LogEntry> entries = new();
    private readonly object gate = new();
    public string Path { get; } = System.IO.Path.Combine(directory, "BuildLists.log");
    public LogEntry[] Entries { get { lock (gate) return entries.ToArray(); } }
    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");
    private void Write(string level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        lock (gate)
        {
            entries.Enqueue(entry);
            while (entries.Count > 3000) entries.Dequeue();
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(Path) && new FileInfo(Path).Length > 5_000_000) File.Move(Path, Path + ".1", true);
                File.AppendAllText(Path, $"{entry.Time:O} [{level}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) { sink.Error(ex, "Artisan's Material Board: ログファイルの書き込みに失敗"); }
        }
        if (level == "ERROR") sink.Error(message);
        else if (level == "WARN") sink.Warning(message);
        else sink.Information(message);
    }
}
