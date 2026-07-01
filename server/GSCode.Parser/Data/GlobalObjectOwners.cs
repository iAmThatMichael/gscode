using System;
using System.Collections.Generic;

namespace GSCode.Parser.Data;

/// <summary>
/// Identifier lexemes recognized as "global object" roots for dot-field access tracking
/// (e.g. <c>level.foo</c>, <c>self.bar</c>). Shared across the parser so any analyser that
/// needs "is this a tracked global owner" doesn't duplicate the list.
/// </summary>
public static class GlobalObjectOwners
{
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        "level",
        "world",
        "self"
    };
}
