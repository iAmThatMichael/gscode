using GSCode.Data.Models.Interfaces;
using GSCode.Data.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Data.Models;

public record class ScrFunction : IExportedSymbol
{
    /// <summary>
    /// The name of the function
    /// </summary>
    [JsonRequired]
    public required string Name { get; set; }

    /// <summary>
    /// The description for this function
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// An example of this function's usage
    /// </summary>
    public string? Example { get; set; }

    /// <summary>
    /// The documentation comment for this function.
    /// </summary>
    public string? DocComment { get; set; }

    /// <summary>
    /// The overloads (variants) of this function
    /// </summary>
    public List<ScrFunctionOverload> Overloads { get; set; } = [];

    /// <summary>
    /// The flags list of this function, which may be empty
    /// </summary>
    public List<string> Flags { get; set; } = [];

    private string? _cachedDocumentation = null;

    // TODO: has been hacked to show first only, but we need to handle all overloads eventually.

    /// <summary>
    /// Yields a documentation hover string for this function. Generated once, then cached.
    /// </summary>
    public string Documentation
    {
        get
        {
            if (_cachedDocumentation is string documentation)
            {
                return documentation;
            }

            // If DocComment is present (from user-defined script doc), use it directly
            // as it's already fully formatted by SanitizeDocForMarkdown
            if (!string.IsNullOrWhiteSpace(DocComment))
            {
                _cachedDocumentation = DocComment;
                return _cachedDocumentation;
            }

            // Otherwise, generate documentation from API-defined properties
            string calledOnString = Overloads.First().CalledOn is ScrFunctionArg calledOn ? $"{calledOn.Name} " : string.Empty;

            // Built-in API functions have Description populated from JSON and don't show "function" keyword
            // Script functions either use DocComment (already handled above) or have minimal metadata
            // If we're here generating docs AND have Description, it's a built-in API
            bool isBuiltInApi = !string.IsNullOrWhiteSpace(Description);
            string functionKeyword = isBuiltInApi ? string.Empty : "function ";

            _cachedDocumentation =
                $"""
                ```gsc
                {calledOnString}{functionKeyword}{Name}({GetCodedParameterList()})
                ```
                ---
                {GetDescriptionString()}
                {GetParametersString()}
                {GetExampleString()}
                {GetFlagsString()}
                """;

            return _cachedDocumentation;
        }
    }

    private string GetDescriptionString() => FunctionDocumentationFormatter.FormatDescription(Description);

    private string GetCodedParameterList()
    {
        if (Overloads.First().Parameters.Count == 0)
        {
            return string.Empty;
        }

        return FunctionDocumentationFormatter.FormatParameterList(
            Overloads.First().Parameters,
            p => p.Name,
            p => p.Mandatory);
    }

    private string GetParametersString() =>
        FunctionDocumentationFormatter.FormatParametersSection(
            Overloads.First().Parameters,
            Overloads.First().CalledOn,
            p => p.Name,
            p => p.Mandatory,
            p => p.Description,
            c => c.Name);

    private string GetExampleString() => FunctionDocumentationFormatter.FormatExample(Example);

    private string GetFlagsString() => FunctionDocumentationFormatter.FormatFlags(Flags);

    /// <summary>
    /// The namespace of this function.
    /// </summary>
    public string Namespace { get; set; } = "sys";

    public ExportedSymbolType Type { get; set; } = ExportedSymbolType.Function;

    /// <summary>
    /// Whether this function's namespace can be skipped, e.g. because it's a built-in
    /// function or it belongs to this script.
    /// </summary>
    public bool Implicit { get; set; } = false;

    /// <summary>
    /// Whether this function is private and cannot be accessed from other scripts.
    /// </summary>
    public bool Private { get; set; } = false;
}


public sealed class ScrFunctionOverload
{
    /// <summary>
    /// The entity that this function is called on
    /// </summary>
    public ScrFunctionArg? CalledOn { get; set; }

    /// <summary>
    /// The parameter list of this function, which may be empty
    /// </summary>
    public List<ScrFunctionArg> Parameters { get; set; } = [];

    /// <summary>
    /// The return value of this function, if any
    /// </summary>
    public ScrFunctionReturn? Returns { get; set; }

    /// <summary>
    /// Whether this function accepts variable arguments (...)
    /// </summary>
    public bool Vararg { get; set; } = false;
}

public record class ScrFunctionReturn
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; } = default!;
    public ScrFunctionDataType? Type { get; set; } = default!;
    public bool? Void { get; set; }
}

public record class ScrFunctionArg
{
    // TODO: this currently is nullable due to issues within the API, it'd be better to enforce not null though asap
    /// <summary>
    /// The name of the parameter
    /// </summary>
    public string Name { get; set; } = default!;

    /// <summary>
    /// The description for this parameter
    /// </summary>
    public string? Description { get; set; } = default!;

    /// <summary>
    /// The type of the parameter
    /// </summary>
    public ScrFunctionDataType? Type { get; set; } = default!;

    /// <summary>
    /// Whether the parameter is mandatory or optional
    /// </summary>
    public bool? Mandatory { get; set; }

    /// <summary>
    /// The default value for this parameter
    /// </summary>
    public ScriptValue? Default { get; set; }
}

public record class ScrFunctionDataType
{
    public string DataType { get; set; } = default!;
    public string? InstanceType { get; set; }
    public bool IsArray { get; set; } = false;
}
