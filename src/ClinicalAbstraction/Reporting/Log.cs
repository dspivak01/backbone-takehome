namespace ClinicalAbstraction.Reporting;

/// <summary>Writes each line to the console and to a log file, so every run leaves an execution log.</summary>
public sealed class Log : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly Lock _gate = new();

    public string? FilePath { get; }

    public Log(string? directory, string command)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{command}.log");
        _file = new StreamWriter(FilePath) { AutoFlush = true };
    }

    public void Info(string message)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff}  {message}";
        lock (_gate)
        {
            Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }

    /// <summary>Writes to the log file only. Used for detail that would crowd the console.</summary>
    public void Detail(string message)
    {
        lock (_gate) _file?.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}  {message}");
    }

    public void Dispose() => _file?.Dispose();
}
