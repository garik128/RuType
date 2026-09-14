using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace RuType.Core;

/// <summary>
/// Лёгкий диагностический лог. Включается, если в каталоге данных есть файл
/// debug.on. Пишет в data\rutype_debug.log. В обычной работе бездействует.
///
/// Запись асинхронная: Line только кладёт строку в очередь, файл пишет фоновый
/// поток пачками. Лог зовётся из обработчика LL-хука, а синхронная дозапись файла
/// на каждое событие удлиняла бы обработчик (риск снятия хука по таймауту).
/// </summary>
public static class Log
{
    private static readonly bool _enabled;
    private static readonly string _path;
    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly AutoResetEvent _signal = new(false);
    private static readonly object _writeLock = new();

    static Log()
    {
        // Портабл: лог и флаг debug.on - в подпапке data рядом с exe.
        string dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        _path = Path.Combine(dataDir, "rutype_debug.log");
        _enabled = File.Exists(Path.Combine(dataDir, "debug.on"));
        if (_enabled)
        {
            _queue.Enqueue($"\n=== старт {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
            new Thread(WriterLoop) { IsBackground = true, Name = "RuType log" }.Start();
        }
    }

    public static bool Enabled => _enabled;

    public static void Line(string s)
    {
        if (!_enabled) return;
        _queue.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {s}\n");
        _signal.Set();
    }

    /// <summary>Дописать накопленное синхронно (при выходе из программы).</summary>
    public static void Flush()
    {
        if (_enabled) Drain();
    }

    private static void WriterLoop()
    {
        while (true)
        {
            _signal.WaitOne(1000);
            Drain();
        }
    }

    private static void Drain()
    {
        lock (_writeLock)
        {
            if (_queue.IsEmpty) return;
            var sb = new StringBuilder();
            while (_queue.TryDequeue(out var line)) sb.Append(line);
            try { File.AppendAllText(_path, sb.ToString()); }
            catch { }
        }
    }
}
