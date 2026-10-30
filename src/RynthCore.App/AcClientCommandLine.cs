using System.Collections.Generic;
using System.Text;

namespace RynthCore.App;

/// <summary>
/// acclient.exe's own command-line rules, and how to write a value so the client reads it back.
///
/// The client splits its command line itself (ConvertCommandLineToArgList, 0x004098F0, called from
/// WinMain; ArgumentParser::ParseCommandLine uses the same template), not with the C runtime's rules:
///   * arguments are split on spaces (only ' ') outside quotes;
///   * a " switches quoting on or off and is dropped; it is never part of an argument;
///   * nothing is escaped: a backslash is an ordinary character;
///   * a quote left open refuses the whole command line ("Unmatched quote"), so the client starts
///     without the account, password or host.
/// The launcher used to write the C runtime's form ("\"" around the value, \\ for \, \" for ") for a
/// value with a space or a quote; the client kept the backslashes, so a password with a quote mark
/// reached the server changed and the login failed (2026-10-21, a 30-character password with most
/// special characters on Conquest; ThwargLauncher, which passes the password as it is, worked).
/// </summary>
internal static class AcClientCommandLine
{
    /// <summary>
    /// <paramref name="value"/> as one argument the client reads back as <see cref="ClientValue"/> of it:
    /// as it is when it has no space (what ThwargLauncher passes), else in quotes. Quote marks in it are
    /// left out, as the client would drop them anyway; that also keeps the quotes balanced.
    /// </summary>
    public static string Token(string? value)
    {
        string v = ClientValue(value);
        if (v.Length == 0)
            return "\"\"";
        return v.IndexOf(' ') >= 0 ? "\"" + v + "\"" : v;
    }

    /// <summary>What the client can receive of <paramref name="value"/>: everything but its quote marks.</summary>
    public static string ClientValue(string? value) => (value ?? string.Empty).Replace("\"", string.Empty);

    /// <summary>True when the client can't receive <paramref name="value"/> as it is (it has a quote mark).</summary>
    public static bool LosesCharacters(string? value) => !string.IsNullOrEmpty(value) && value.IndexOf('"') >= 0;

    /// <summary>
    /// The arguments the client reads from <paramref name="commandLine"/>, by its own rules; null when it
    /// refuses the line (a quote left open). For tests, and for reading the client's -h the way it does.
    /// </summary>
    public static List<string>? Split(string? commandLine)
    {
        var args = new List<string>();
        if (commandLine == null)
            return null;
        var cur = new StringBuilder();
        bool inArg = false, quoted = false;
        foreach (char c in commandLine)
        {
            if (c == ' ' && !quoted)
            {
                if (inArg) { args.Add(cur.ToString()); cur.Clear(); inArg = false; }
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else
            {
                inArg = true;
                cur.Append(c);
            }
        }
        if (quoted)
            return null;
        if (inArg)
            args.Add(cur.ToString());
        return args;
    }
}
