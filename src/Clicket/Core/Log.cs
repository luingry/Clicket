using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Clicket.Core;

/// <summary>
/// Bounded, rotating log on a below-normal writer thread. Callers never block on disk; the writer sleeps on an
/// event while nothing is logged. The user profile path is masked.
/// </summary>
public static class Log
{
    private const int Capacity = 4096;
    private const long MaxBytes = 768 * 1024;
    private static readonly Queue<string> Pending = new Queue<string>();
    private static readonly AutoResetEvent Signal = new AutoResetEvent(false);
    private static string? _path;
    private static Thread? _writer;
    private static string _profile = "";

    public static void Initialize(string path)
    {
        lock (Pending)
        {
            if (_writer != null) return;
            _path = path;
            _profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            _writer = new Thread(Write) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Clicket log" };
            _writer.Start();
        }
    }

    public static void Info(string message) => Enqueue("INFO", message);

    public static void Warn(string message) => Enqueue("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Enqueue("ERROR", exception == null ? message : message + " " + exception.GetType().Name + ": " + exception.Message + Environment.NewLine + exception.StackTrace);

    private static void Enqueue(string level, string message)
    {
        if (_profile.Length > 3) message = message.Replace(_profile, "%USERPROFILE%");
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + message;
        lock (Pending)
        {
            if (_writer == null) return;
            if (Pending.Count >= Capacity) Pending.Dequeue();
            Pending.Enqueue(line);
        }
        Signal.Set();
    }

    private static void Write()
    {
        var batch = new StringBuilder();
        while (true)
        {
            Signal.WaitOne();
            try
            {
                batch.Clear();
                lock (Pending)
                {
                    while (Pending.Count > 0) batch.AppendLine(Pending.Dequeue());
                }
                if (batch.Length == 0 || _path == null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var file = new FileInfo(_path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    var old = _path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
                File.AppendAllText(_path, batch.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
