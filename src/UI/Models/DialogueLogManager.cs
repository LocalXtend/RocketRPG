#nullable enable
using System;
using System.Collections.Generic;

namespace RocketRPG.Models;

/// <summary>
/// Thread-safe, RAM-bounded (max 100 entries) in-memory dialogue log storage.
/// Automatically evicts older entries (FIFO) to prevent memory growth.
/// </summary>
public static class DialogueLogManager
{
    private const int MaxEntries = 100;
    private static readonly List<string> _entries = new();
    private static readonly object _lock = new();

    public static event Action? LogUpdated;

    public static void Add(string text) => AddEntry(text);

    public static void AddEntry(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string clean = text.Trim();

        lock (_lock)
        {
            // Avoid duplicate consecutive identical lines
            if (_entries.Count > 0 && _entries[^1].Equals(clean, StringComparison.Ordinal))
                return;

            _entries.Add(clean);
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0);
            }
        }

        try { LogUpdated?.Invoke(); } catch { }
    }

    public static List<string> GetEntries()
    {
        lock (_lock)
        {
            return new List<string>(_entries);
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
        try { LogUpdated?.Invoke(); } catch { }
    }
}
