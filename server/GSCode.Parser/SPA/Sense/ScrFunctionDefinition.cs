using GSCode.Parser.Data;
using GSCode.Data.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.SPA.Sense;

/// <summary>
/// SPA IntelliSense component for defined functions
/// </summary>
public sealed class ScrFunctionDefinition
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

            string calledOnString = Overloads.First().CalledOn is ScrFunctionParameter calledOn ? $"{calledOn.Name} " : string.Empty;

            _cachedDocumentation =
                $"""
                ```gsc
                {calledOnString}function {Name}({GetCodedParameterList()})
                ```
                ---
                {GetDescriptionString()}
                {GetParametersString()}
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

    private string GetFlagsString() => FunctionDocumentationFormatter.FormatFlags(Flags);
}

public sealed class ScrFunctionOverload
{
    /// <summary>
    /// The entity that this function is called on
    /// </summary>
    public ScrFunctionParameter? CalledOn { get; set; }

    /// <summary>
    /// The parameter list of this function, which may be empty
    /// </summary>
    public List<ScrFunctionParameter> Parameters { get; set; } = [];
}

/// <summary>
/// SPA IntelliSense component for defined functions' parameters
/// </summary>
public sealed class ScrFunctionParameter
{
    // TODO: this currently is nullable due to issues within the API, it'd be better to enforce not null though asap
    /// <summary>
    /// The name of the parameter
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The description for this parameter
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether the parameter is mandatory or optional
    /// </summary>
    public required bool? Mandatory { get; set; }

    // Type: May be implemented in future
}