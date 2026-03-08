using GSCode.Data.Models.Interfaces;
using GSCode.Data.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Data.Models;

public record class ScrClass : IExportedSymbol
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; } = default!;
    public string? InheritsFrom { get; set; } = default!;

    // TODO: need to account for that members are only scoped to functions that occur after their definition.
    public List<ScrFunction> Methods { get; set; } = [];
    public List<ScrMember> Members { get; set; } = [];

    public ExportedSymbolType Type { get; set; } = ExportedSymbolType.Class;
}

public record class ScrMember
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; } = default!;
    public string? DocComment { get; set; } = default!;

    private string? _cachedDocumentation = null;

    /// <summary>
    /// Yields formatted documentation for this member. Generated once, then cached.
    /// </summary>
    public string Documentation
    {
        get
        {
            // Check if we've already generated and cached documentation
            if (_cachedDocumentation is string documentation)
            {
                return documentation;
            }

            // If DocComment is present (from user-defined script doc), format it
            if (!string.IsNullOrWhiteSpace(DocComment))
            {
                // Note: Members don't have a namespace context, so pass null
                _cachedDocumentation = ScriptDocCommentFormatter.FormatToMarkdown(DocComment, null);
                return _cachedDocumentation;
            }

            // Otherwise, generate simple documentation
            _cachedDocumentation = !string.IsNullOrWhiteSpace(Description) 
                ? Description 
                : $"```gsc\n{Name}\n```";

            return _cachedDocumentation;
        }
    }
}