using System;
using System.Collections.Generic;

namespace GSCode.Parser.Data;

/// <summary>
/// Shared GSC/CSC language keywords used across parsing and completion features.
/// </summary>
internal static class ScriptKeywords
{
    /// <summary>
    /// All recognized keywords in GSC/CSC languages.
    /// </summary>
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "return", "wait", "thread", "classes", "if", "else", "do", "while",
        "for", "foreach", "in", "new", "waittill", "waittillmatch", "waittillframeend",
        "switch", "case", "default", "break", "continue", "notify", "endon",
        "waitrealtime", "profilestart", "profilestop", "isdefined", "vectorscale",
        // Additional keywords
        "true", "false", "undefined", "self", "level", "game", "world", "vararg", "anim",
        "var", "const", "function", "private", "autoexec", "constructor", "destructor"
    };
}
