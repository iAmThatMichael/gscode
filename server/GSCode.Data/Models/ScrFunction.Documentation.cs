using GSCode.Data.Helpers;
using System.Text;

namespace GSCode.Data.Models;

public partial record class ScrFunction
{
    private string? _cachedDocumentation = null;

    /// <summary>
    /// Lazily builds and caches the markdown hover string for this function.
    /// </summary>
    public string Documentation
    {
        get
        {
            if (_cachedDocumentation is string cached) return cached;

            if (!string.IsNullOrWhiteSpace(DocComment))
            {
                // Always build the prototype from live ScrFunction data — the Name: field in
                // the doc comment text is author-written and may have wrong/empty parameters.
                // FormatBodyOnly renders summary, parameters, region, examples without the Name: block.
                var docOverload = Overloads.FirstOrDefault();
                string docCalledOn = docOverload?.CalledOn is ScrFunctionArg dco ? $"{dco.Name} " : string.Empty;
                string docFnKeyword = IsBuiltIn ? string.Empty : "function ";
                string prototype = $"```gsc\n{docCalledOn}{docFnKeyword}{Name}({GetCodedParameterList(docOverload)})\n```\n---";
                string body = ScriptDocCommentFormatter.FormatBodyOnly(DocComment, Namespace);
                return _cachedDocumentation = string.IsNullOrWhiteSpace(body)
                    ? prototype
                    : $"{prototype}\n{body}";
            }

            if (Overloads.Count <= 1)
            {
                var overload = Overloads.FirstOrDefault();
                string calledOn = overload?.CalledOn is ScrFunctionArg co ? $"{co.Name} " : string.Empty;
                string fnKeyword = IsBuiltIn ? string.Empty : "function ";

                _cachedDocumentation =
                    $"""
                    ```gsc
                    {calledOn}{fnKeyword}{Name}({GetCodedParameterList(overload)})
                    ```
                    ---
                    {GetDescriptionString()}
                    {GetParametersString(overload)}
                    {GetFlagsString()}
                    """;
            }
            else
            {
                StringBuilder sb = new();
                sb.AppendLine("```gsc");
                string fnKeyword = IsBuiltIn ? string.Empty : "function ";
                for (int i = 0; i < Overloads.Count; i++)
                {
                    var overload = Overloads[i];
                    string calledOn = overload.CalledOn is ScrFunctionArg co ? $"{co.Name} " : string.Empty;
                    sb.AppendLine($"// Overload {i + 1}");
                    sb.AppendLine($"{calledOn}{fnKeyword}{Name}({GetCodedParameterList(overload)})");
                }
                sb.AppendLine("```");
                sb.AppendLine("---");

                string desc = GetDescriptionString();
                if (!string.IsNullOrEmpty(desc)) sb.AppendLine(desc);

                for (int i = 0; i < Overloads.Count; i++)
                {
                    string paramStr = GetParametersString(Overloads[i], $"**Overload {i + 1}** ");
                    if (!string.IsNullOrEmpty(paramStr)) sb.AppendLine(paramStr);
                }

                string flags = GetFlagsString();
                if (!string.IsNullOrEmpty(flags)) sb.Append(flags);

                _cachedDocumentation = sb.ToString().TrimEnd();
            }

            return _cachedDocumentation;
        }
    }

    private string GetDescriptionString() =>
        FunctionDocumentationFormatter.FormatDescription(Description);

    private string GetCodedParameterList(ScrFunctionOverload? overload)
    {
        if (overload is null || overload.Parameters.Count == 0) return string.Empty;
        return FunctionDocumentationFormatter.FormatParameterList(
            overload.Parameters,
            p => p.Name,
            p => p.Mandatory);
    }

    private string GetParametersString(ScrFunctionOverload? overload, string prefix = "")
    {
        if (overload is null) return string.Empty;
        // Built-in API functions show CalledOn in the signature, so don't repeat it in the parameters section
        var calledOn = IsBuiltIn ? null : overload.CalledOn;
        return FunctionDocumentationFormatter.FormatParametersSection(
            overload.Parameters,
            calledOn,
            p => p.Name,
            p => p.Mandatory,
            p => p.Description,
            c => c.Name);
    }

    private string GetFlagsString() =>
        FunctionDocumentationFormatter.FormatFlags(Flags);
}
