using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Turbo-650 performance: append-only JSONL persistence for high-frequency
///     logs. Flushing becomes O(new entries) instead of re-reading, re-serializing
///     and rewriting the whole store file on every flush (the old pattern cost a
///     full file rewrite up to thousands of entries every few seconds while chat
///     was active). Legacy JSON-array files keep working: they are read fine and
///     migrated to JSONL on the first append. A torn trailing line (crash during
///     append) is skipped instead of failing the whole file.
/// </summary>
internal static class JsonLines
{
    /// <summary>Reads entries from a JSONL file or a legacy JSON-array file.</summary>
    public static List<T> Read<T>(string path, JsonSerializerOptions options)
    {
        var result = new List<T>();
        if (!File.Exists(path))
        {
            return result;
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        if (text.TrimStart()[0] == '[')
        {
            // Legacy format: one JSON array document.
            var legacy = JsonSerializer.Deserialize<List<T>>(text, options);
            if (legacy != null)
            {
                result.AddRange(legacy);
            }

            return result;
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<T>(trimmed, options);
                if (entry != null)
                {
                    result.Add(entry);
                }
            }
            catch
            {
                // Skip a torn/partial trailing line rather than losing the file.
            }
        }

        return result;
    }

    /// <summary>
    ///     Appends entries as one JSON value per line (compact, never indented).
    ///     A legacy JSON-array file is migrated to JSONL exactly once. When
    ///     maxBytes is set and the file outgrows the budget, it is compacted to
    ///     the most recent maxEntries values (rare, amortized O(1) per append).
    /// </summary>
    public static void Append<T>(string path, IEnumerable<T> entries, JsonSerializerOptions options, long? maxBytes = null, int maxEntries = int.MaxValue)
    {
        var list = entries as IList<T> ?? entries.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var isLegacy = false;
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path);
            int ch;
            while ((ch = reader.Read()) >= 0)
            {
                if (!char.IsWhiteSpace((char)ch))
                {
                    isLegacy = (char)ch == '[';
                    break;
                }
            }
        }

        if (isLegacy)
        {
            // One-time migration: fold the legacy array and the new batch into
            // a single JSONL document.
            var legacy = Read<T>(path, options);
            legacy.AddRange(list);
            if (legacy.Count > maxEntries)
            {
                legacy = legacy.Skip(legacy.Count - maxEntries).ToList();
            }

            Rewrite(path, legacy, options);
            return;
        }

        var sb = new StringBuilder();
        foreach (var entry in list)
        {
            sb.AppendLine(JsonSerializer.Serialize(entry, Compact(options)));
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.AppendAllText(path, sb.ToString());

        if (maxBytes.HasValue && new FileInfo(path).Length > maxBytes.Value)
        {
            var all = Read<T>(path, options);
            if (all.Count > maxEntries)
            {
                all = all.Skip(all.Count - maxEntries).ToList();
            }

            Rewrite(path, all, options);
        }
    }

    /// <summary>Line serialization must stay single-line even if the caller's options are indented.</summary>
    private static JsonSerializerOptions Compact(JsonSerializerOptions options)
    {
        if (!options.WriteIndented)
        {
            return options;
        }

        return new JsonSerializerOptions(options) { WriteIndented = false };
    }

    private static void Rewrite<T>(string path, List<T> entries, JsonSerializerOptions options)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.AppendLine(JsonSerializer.Serialize(entry, Compact(options)));
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        File.Move(tmp, path, true);
    }
}