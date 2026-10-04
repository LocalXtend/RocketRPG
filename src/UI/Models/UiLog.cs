#nullable enable
using System;
using System.IO;
using System.Text;

namespace RocketRPG.Models;

public static class UiLog
{
    static readonly object Lock = new();

    public static string LogPath =>
        System.IO.Path.Combine(SettingsService.Root(), "config", "ui.log");

    public static void Write(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n";
        TryAppend(LogPath, line);
        var fallback = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RocketRPG", "ui.log");
        TryAppend(fallback, line);
    }

    static void TryAppend(string file, string line)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                System.IO.File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch { }
    }
}
