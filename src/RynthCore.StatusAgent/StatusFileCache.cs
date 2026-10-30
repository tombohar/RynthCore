using System.Collections.Concurrent;
using System.Text;

namespace RynthCore.StatusAgent;

/// <summary>
/// Reading the per-client files the RynthRemote plugin rewrites several times a second, without ever
/// dropping a client from a snapshot.
///
/// <para>Before (2026-10-05): while the agent was reading a status file, the plugin's atomic replace
/// (File.Move over it) failed (Windows won't replace an open file), and RynthRemote 0.3 then fell back to
/// overwriting the file in place; the next read caught it empty or half-written and the parse failed.
/// That client was left out of that one snapshot (StatusAgent.log: "1 client(s)" for ~60 ms, several
/// times a minute), and the phone's open deck for it unmounted and came back: the screen flashed and
/// scrolled to the top. RynthRemote 0.4 skips such a write instead of overwriting in place.</para>
///
/// <para>Here: a status file that can't be read or parsed is answered with the last good parse of that
/// file for up to <see cref="KeepLastGood"/> (its own timestamp stays in the model, so its age still shows
/// honestly), and files are read with ReadWrite|Delete sharing so a reader never makes a writer fail.</para>
/// </summary>
internal static class StatusFileCache
{
    public static readonly TimeSpan KeepLastGood = TimeSpan.FromSeconds(3);

    private static readonly ConcurrentDictionary<string, (object Model, DateTime AtUtc)> LastGood = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Read a whole file while letting the writer replace or rewrite it underneath.</summary>
    public static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        using var ms = new MemoryStream(fs.CanSeek ? (int)Math.Min(fs.Length, int.MaxValue) : 4096);
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    public static string ReadAllTextShared(string path) => Encoding.UTF8.GetString(ReadAllBytesShared(path)).TrimStart('﻿');

    /// <summary>
    /// Parse <paramref name="path"/> with <paramref name="parse"/>; on any read or parse failure (or a null
    /// parse) answer the last good result for that path if it is younger than <see cref="KeepLastGood"/>.
    /// <paramref name="read"/> is the file reader (tests pass their own).
    /// </summary>
    public static T? Read<T>(string path, Func<string, T?> parse, DateTime nowUtc, Func<string, string>? read = null) where T : class
    {
        try
        {
            T? model = parse((read ?? ReadAllTextShared)(path));
            if (model != null)
            {
                LastGood[path] = (model, nowUtc);
                return model;
            }
        }
        catch (Exception ex)
        {
            AgentLog.Debug($"status file '{path}' unreadable: {ex.GetType().Name}: {ex.Message}");
        }
        if (LastGood.TryGetValue(path, out var lg) && nowUtc - lg.AtUtc <= KeepLastGood && lg.Model is T t)
            return t;
        return null;
    }

    /// <summary>Drop what is remembered for a file that is gone (a retired dead client) or for every file
    /// not in <paramref name="keep"/>.</summary>
    public static void Forget(string path) => LastGood.TryRemove(path, out _);

    public static void ForgetAllBut(IReadOnlyCollection<string> keep)
    {
        var set = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (var k in LastGood.Keys)
            if (!set.Contains(k)) LastGood.TryRemove(k, out _);
    }

    internal static int CountForTests => LastGood.Count;
}
