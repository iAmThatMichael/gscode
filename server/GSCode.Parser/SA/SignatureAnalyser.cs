using System.Text;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Text.RegularExpressions;
using GSCode.Parser.Util;

namespace GSCode.Parser.SA;

internal ref struct SignatureAnalyser(ScriptNode rootNode, DefinitionsTable definitionsTable, ParserIntelliSense sense)
{
    private static readonly Regex s_kv = new(@"^(?<k>\w+)\s*:\s*(?<v>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // Accept <arg> desc, <arg>: desc, [arg] desc, [arg]: desc, or bareword desc
    private static readonly Regex s_argPattern = new(@"^(?<n><[^>]+>|\[[^\]]+\]|[^:\s]+)\s*:??\s*(?<d>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private ScriptNode RootNode { get; } = rootNode;
    private DefinitionsTable DefinitionsTable { get; } = definitionsTable;
    private ParserIntelliSense Sense { get; } = sense;

    public void Analyse()
    {
        foreach (AstNode scriptDependency in RootNode.Dependencies)
        {
            switch (scriptDependency.NodeType)
            {
                case AstNodeType.Dependency:
                    AnalyseDependency((DependencyNode)scriptDependency);
                    break;
            }
        }

        foreach (AstNode scriptDefn in RootNode.ScriptDefns)
        {
            switch (scriptDefn.NodeType)
            {
                case AstNodeType.FunctionDefinition:
                    AnalyseFunction((FunDefnNode)scriptDefn);
                    break;
                case AstNodeType.Namespace:
                    AnalyseNamespace((NamespaceNode)scriptDefn);
                    break;
                case AstNodeType.ClassDefinition:
                    AnalyseClass((ClassDefnNode)scriptDefn);
                    break;
                case AstNodeType.DevBlock:
                    AnalyseDevBlock((DefnDevBlockNode)scriptDefn);
                    break;
            }
        }
    }

    public void AnalyseClass(ClassDefnNode classDefn)
    {
        if (classDefn.NameToken is not Token nameToken)
        {
            return;
        }

        string name = nameToken.Lexeme;

        // Create a class definition
        ScrClass scrClass = new()
        {
            Name = name,
            Description = null, // TODO: Check the DOC COMMENT
            InheritsFrom = classDefn.InheritsFromToken?.Lexeme,
        };

        foreach (AstNode child in classDefn.Body.Definitions)
        {
            switch (child.NodeType)
            {
                case AstNodeType.FunctionDefinition:
                    AnalyseClassFunction(scrClass, (FunDefnNode)child);
                    break;
                case AstNodeType.ClassMember:
                    AnalyseClassMember(scrClass, (MemberDeclNode)child);
                    break;
            }
        }

        // Record class location for go-to-definition
        DefinitionsTable.AddClassLocation(DefinitionsTable.CurrentNamespace, name, Sense.ScriptPath, nameToken.TokenRange);

        // Add class to definitions table for CFG and RDA analysis
        DefinitionsTable.AddClass(scrClass, classDefn);

        Sense.AddSenseToken(nameToken, new ScrClassSymbol(nameToken, scrClass));

        // Enable GoTo Definition for base class name
        if (classDefn.InheritsFromToken is Token baseClassToken)
        {
            Sense.AddSenseToken(baseClassToken, new ScrClassReferenceSymbol(baseClassToken, baseClassToken.Lexeme));
        }
    }

    private void AnalyseClassFunction(ScrClass scrClass, FunDefnNode functionDefn)
    {
        if (functionDefn.Name is not Token nameToken)
        {
            return;
        }

        string name = nameToken.Lexeme;

        // Extract raw doc comment token
        Token? docCommentToken = FindDocCommentTokenBefore(nameToken);

        // Parse mandatory parameters from raw text
        var mandatoryParams = ExtractMandatoryParametersFromRaw(docCommentToken?.Lexeme);

        // Store the SANITIZED (but not markdown-formatted) doc comment
        string? doc = docCommentToken != null 
            ? DocCommentHelper.Sanitize(docCommentToken.Lexeme)
            : null;

        // Analyze the parameter list with mandatory parameter information
        IEnumerable<ScrParameter> parameters = AnalyseFunctionParameters(functionDefn.Parameters, mandatoryParams);

        // TODO: Probably needs to be a ScrMethod instead.
        ScrFunction function = new()
        {
            Name = name,
            Description = null,
            DocComment = doc,
            Overloads = [
                new ScrFunctionOverload()
                {
                    Parameters = GetParametersAsRecord(parameters)!,
                    CalledOn = null, // Class methods don't need CalledOn - it's implicit
                    Returns = null!,
                    Vararg = functionDefn.Parameters.Vararg
                }
            ],

            Flags = [],
            Private = functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Private)
        };

        // Produce a definition for our function
        scrClass.Methods.Add(function);

        // Record method/function location (method recorded as function under containing namespace)
        DefinitionsTable.AddFunctionLocation(DefinitionsTable.CurrentNamespace, name, Sense.ScriptPath, nameToken.TokenRange);
        // Record parameter names for outline/signature
        DefinitionsTable.RecordFunctionParameters(DefinitionsTable.CurrentNamespace, name, (function.Overloads[0].Parameters ?? new List<ScrFunctionArg>()).Select(a => a.Name));
        // Record flags (private, autoexec)
        var flags = new List<string>();
        if (function.Private)
        {
            flags.Add("private");
        }
        if (functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Autoexec))
        {
            flags.Add("autoexec");
        }
        DefinitionsTable.RecordFunctionFlags(DefinitionsTable.CurrentNamespace, name, flags);

        // Record doc comment if present
        DefinitionsTable.RecordFunctionDoc(DefinitionsTable.CurrentNamespace, name, doc);

        // NEW: Also record under the class name as its own qualifier so ClassName::Method() resolves
        string classNs = scrClass.Name;
        DefinitionsTable.AddFunctionLocation(classNs, name, Sense.ScriptPath, nameToken.TokenRange);
        DefinitionsTable.RecordFunctionParameters(classNs, name, (function.Overloads[0].Parameters ?? new List<ScrFunctionArg>()).Select(a => a.Name));
        DefinitionsTable.RecordFunctionFlags(classNs, name, flags);
        DefinitionsTable.RecordFunctionDoc(classNs, name, doc);

        Sense.AddSenseToken(nameToken, new ScrMethodSymbol(nameToken, function, scrClass));

        if (parameters is not null)
        {
            int paramIndex = 0;
            foreach (ScrParameter parameter in parameters)
            {
                // Get the corresponding ParamNode for this parameter
                ParamNode? paramNode = functionDefn.Parameters.Parameters.ElementAtOrDefault(paramIndex);
                Sense.AddSenseToken(parameter.Source, new ScrParameterSymbol(parameter, paramNode));
                paramIndex++;
            }
        }
    }

    private void AnalyseClassMember(ScrClass scrClass, MemberDeclNode memberDecl)
    {
        if (memberDecl.NameToken is not Token nameToken)
        {
            return;
        }

        // Extract raw doc comment token and sanitize it
        Token? docCommentToken = FindDocCommentTokenBefore(nameToken);
        string? doc = docCommentToken != null 
            ? DocCommentHelper.Sanitize(docCommentToken.Lexeme)
            : null;

        ScrMember member = new()
        {
            Name = memberDecl.NameToken?.Lexeme ?? "",
            Description = null,
            DocComment = doc
        };

        scrClass.Members.Add(member);
        Sense.AddSenseToken(nameToken, new ScrClassMemberSymbol(nameToken, member, scrClass));
    }

    public void AnalyseDependency(DependencyNode dependencyNode)
    {
        string? dependencyPath = Sense.GetDependencyPath(dependencyNode.Path, dependencyNode.Range);
        if (dependencyPath is null)
        {
            return;
        }

        Sense.AddSenseToken(dependencyNode.FirstPathToken, new ScrDependencySymbol(dependencyNode.Range, dependencyPath, dependencyNode.Path));

        // Add the dependency to the list
        DefinitionsTable.AddDependency(dependencyPath);
    }

    public void AnalyseNamespace(NamespaceNode namespaceNode)
    {
        // Change the namespace at this point and onwards
        DefinitionsTable.CurrentNamespace = namespaceNode.NamespaceIdentifier;
    }

    public void AnalyseDevBlock(DefnDevBlockNode devBlockNode)
    {
        // Recursively analyze all definitions within the devblock
        foreach (AstNode scriptDefn in devBlockNode.Definitions)
        {
            switch (scriptDefn.NodeType)
            {
                case AstNodeType.FunctionDefinition:
                    AnalyseFunction((FunDefnNode)scriptDefn);
                    break;
                case AstNodeType.Namespace:
                    AnalyseNamespace((NamespaceNode)scriptDefn);
                    break;
                case AstNodeType.ClassDefinition:
                    AnalyseClass((ClassDefnNode)scriptDefn);
                    break;
                case AstNodeType.DevBlock:
                    AnalyseDevBlock((DefnDevBlockNode)scriptDefn);
                    break;
            }
        }
    }

    public void AnalyseFunction(FunDefnNode functionDefn)
    {
        // Get the name of the function - if it's unnamed then it's one that was produced in recovery. No use to us.
        if (functionDefn.Name is not Token nameToken)
        {
            return;
        }

        string name = nameToken.Lexeme;

        // Extract raw doc comment token
        Token? docCommentToken = FindDocCommentTokenBefore(nameToken);

        // Parse mandatory parameters from raw text
        var mandatoryParams = ExtractMandatoryParametersFromRaw(docCommentToken?.Lexeme);

        // Store the SANITIZED (but not markdown-formatted) doc comment
        // The Documentation property will format it later when needed
        string? docComment = docCommentToken != null 
            ? DocCommentHelper.Sanitize(docCommentToken.Lexeme)
            : null;

        // Analyze the parameter list with mandatory parameter information
        IEnumerable<ScrParameter> parameters = AnalyseFunctionParameters(functionDefn.Parameters, mandatoryParams);

        ScrFunction function = new()
        {
            Name = name,
            Description = null, // TODO: Check the DOC COMMENT
            Overloads = [
                new ScrFunctionOverload()
                {
                    CalledOn = null, // TODO: Check the DOC COMMENT
                    Parameters = GetParametersAsRecord(parameters)!,
                    Returns = null!, // TODO: Check the DOC COMMENT
                    Vararg = functionDefn.Parameters.Vararg
                }
            ],
            Flags = [],
            Private = functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Private),
            DocComment = docComment
        };

        // Produce a definition for our function
        DefinitionsTable.AddFunction(function, functionDefn);

        // Record function location for go-to-definition
        DefinitionsTable.AddFunctionLocation(DefinitionsTable.CurrentNamespace, name, Sense.ScriptPath, nameToken.TokenRange);
        // Record parameter names for outline/signature
        DefinitionsTable.RecordFunctionParameters(DefinitionsTable.CurrentNamespace, name, (function.Overloads[0].Parameters ?? new List<ScrFunctionArg>()).Select(a => a.Name));
        // Record flags (private, autoexec)
        var flags = new List<string>();
        if (function.Private)
        {
            flags.Add("private");
        }
        if (functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Autoexec))
        {
            flags.Add("autoexec");
        }
        DefinitionsTable.RecordFunctionFlags(DefinitionsTable.CurrentNamespace, name, flags);

        // Record doc comment if present
        DefinitionsTable.RecordFunctionDoc(DefinitionsTable.CurrentNamespace, name, function.DocComment);

        Sense.AddSenseToken(nameToken, new ScrFunctionSymbol(nameToken, function));

        if (parameters is not null)
        {
            int paramIndex = 0;
            foreach (ScrParameter parameter in parameters)
            {
                // Get the corresponding ParamNode for this parameter
                ParamNode? paramNode = functionDefn.Parameters.Parameters.ElementAtOrDefault(paramIndex);
                Sense.AddSenseToken(parameter.Source, new ScrParameterSymbol(parameter, paramNode));
                paramIndex++;
            }
        }
    }

    private static List<ScrFunctionArg>? GetParametersAsRecord(IEnumerable<ScrParameter> parameters)
    {
        List<ScrFunctionArg> result = new();
        foreach (ScrParameter parameter in parameters)
        {
            result.Add(new ScrFunctionArg()
            {
                Name = parameter.Name,
                Description = null, // TODO: Check the DOC COMMENT
                Type = null, // TODO: Check the DOC COMMENT
                // Read the Mandatory flag directly from the ScrParameter
                Mandatory = parameter.Mandatory,
                Default = null // Not sure we can populate this
            });
        }

        return result;
    }

    private List<ScrParameter> AnalyseFunctionParameters(ParamListNode parameters, HashSet<string> mandatoryParams)
    {
        List<ScrParameter> result = new();
        foreach (ParamNode parameter in parameters.Parameters)
        {
            if (parameter.Name is not Token nameToken)
            {
                continue;
            }

            string name = parameter.Name.Lexeme;
            bool byRef = parameter.ByRef;
            // Check if this parameter is marked as mandatory in the doc comment
            bool isMandatory = mandatoryParams.Contains(name);

            if (parameter.Default is null)
            {
                result.Add(new ScrParameter(name, nameToken, nameToken.Range, byRef, Default: null, Mandatory: isMandatory));
                continue;
            }

            // TODO: do we need to handle defaults now, or leave till later?
            result.Add(new ScrParameter(name, nameToken, nameToken.Range, byRef, parameter.Default, Mandatory: isMandatory));
        }

        return result;
    }

    private static Token? FindDocCommentTokenBefore(Token nameToken)
    {
        DocumentTokensLibrary tokens = Sense.Tokens;
        int idx = tokens.IndexOf(nameToken);
        if (idx < 0)
        {
            return null;
        }

        // Walk backwards to find the line break before the function definition
        while (idx > 0)
        {
            Token? t = tokens.GetAt(idx);
            if (t == null)
            {
                return null;
            }
            if (t.Type == TokenType.LineBreak)
            {
                break;
            }
            idx--;
        }

        // Now scan backwards up to 50 tokens looking for a doc comment
        int count = 50;
        while (count > 0 && idx > 0)
        {
            idx--;
            count--;

            Token? t = tokens.GetAt(idx);
            if (t == null)
            {
                return null;
            }
        }

        if (nameToken.Previous.Type == TokenType.DocComment)
        {
            return nameToken.Previous;
            if (t.Type == TokenType.DocComment)
            {
                return SanitizeDocForMarkdown(t.Lexeme);
            }
        }

        return null;
    }

    private static HashSet<string> ExtractMandatoryParametersFromRaw(string? rawDocComment)
    {
        var mandatoryParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(rawDocComment))
        {
            return mandatoryParams;
        }

        // Parse RAW doc comment (before markdown formatting) for MandatoryArg entries
        // Use DocCommentHelper.Sanitize to get clean lines but don't format to markdown yet
        string[] lines = DocCommentHelper.Sanitize(rawDocComment).Split('\n');
        foreach (var line in lines)
        {
            var match = s_kv.Match(line);
            if (!match.Success) continue;

            string key = match.Groups["k"].Value.Trim().ToLowerInvariant();
            string val = match.Groups["v"].Value.Trim();

            if (key == "mandatoryarg")
            {
                var argMatch = s_argPattern.Match(val);
                if (argMatch.Success)
                {
                    string paramName = argMatch.Groups["n"].Value.Trim();
                    // Remove angle brackets and square brackets
                    paramName = paramName.Replace("<", "").Replace(">", "").Replace("[", "").Replace("]", "");
                    mandatoryParams.Add(paramName);
                }
            }
        }

        return mandatoryParams;
    }

    private static string? BuildPrototype(string? ns, string name, IEnumerable<ScrFunctionArg>? args)
    {
        string paramList = args is null ? string.Empty : string.Join(", ", args.Select(a => a.Name));
        string nsPrefix = string.IsNullOrEmpty(ns) ? string.Empty : ns + "::";
        return $"function {nsPrefix}{name}({paramList})";
    }
}


/// <summary>
/// Records the definition of a function parameter for semantics & hovers
/// </summary>
/// <param name="Source">The parameter source</param>
internal record ScrParameterSymbol(ScrParameter Source) : ISenseDefinition
{
    // I'm pretty sure this is redundant

    public Range Range { get; } = Source.Range;

    public string SemanticTokenType { get; } = "parameter";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover()
    {
        string parameterName = $"{Source.Name}";
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n{0}\n```",
                   parameterName)
            })
        };
    }
}

internal record ScrFunctionSymbol(Token NameToken, ScrFunction Source) : ISenseDefinition
{
    public virtual Range Range { get; } = NameToken.Range;

    public virtual string SemanticTokenType { get; } = "function";
    public virtual string[] SemanticTokenModifiers { get; } = [];

    public virtual Hover GetHover()
    {
        // Prefer user doc comment, if present
        if (!string.IsNullOrWhiteSpace(Source.DocComment))
        {
            return new()
            {
                Range = Range,
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
                {
                    Kind = MarkupKind.Markdown,
                    Value = Source.DocComment
                })
            };
        }

        // For API functions with overloads, use the Documentation property which
        // handles multi-overload display. For single-overload, build inline.
        if (Source.Overloads.Count > 1)
        {
            return new()
            {
                Range = Range,
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
                {
                    Kind = MarkupKind.Markdown,
                    Value = Source.Documentation
                })
            };
        }

        StringBuilder builder = new();

        builder.AppendLine("```gsc");
        builder.Append($"function {Source.Name}(");

        bool first = true;
        foreach (ScrFunctionArg parameter in Source.Overloads[0].Parameters ?? [])
        {
            AppendParameter(builder, parameter, ref first);
        }
        builder.AppendLine(")");
        builder.AppendLine("```");

        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = Source.Documentation
            })
        };
    }

    protected static void AppendParameter(StringBuilder builder, ScrFunctionArg parameter, ref bool first)
    {
        if (!first)
        {
            builder.Append(", ");
        }
        first = false;

        if (parameter.Type is null)
        {
            builder.Append($"{parameter.Name}");
            return;
        }

        builder.Append($"/@ {parameter.Type} @/ {parameter.Name}");
    }
}

internal record ScrClassSymbol(Token NameToken, ScrClass Source) : ISenseDefinition
{

    public Range Range { get; } = NameToken.Range;

    public string SemanticTokenType { get; } = "class";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover()
    {
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = "```gsc\nclass " + Source.Name + "\n```"
            })
        };
    }
}

internal record ScrClassReferenceSymbol(Token NameToken, string ClassName) : ISenseDefinition
{

    public Range Range { get; } = NameToken.Range;

    public string SemanticTokenType { get; } = "class";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover()
    {
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = "```gsc\nclass " + ClassName + "\n```"
            })
        };
    }
}


internal record ScrClassMemberSymbol(Token NameToken, ScrMember Source, ScrClass ClassSource) : ISenseDefinition
{

    public Range Range { get; } = NameToken.Range;

    public string SemanticTokenType { get; } = "property";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover()
    {
        // Always use the Documentation property which handles formatting
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = Source.Documentation
            })
        };
    }
}

internal record ScrMethodSymbol(Token NameToken, ScrFunction Source, ScrClass ClassSource) : ScrFunctionSymbol(NameToken, Source)
{
    public override Range Range { get; } = NameToken.Range;

    public override string SemanticTokenType { get; } = "method";
    public override string[] SemanticTokenModifiers { get; } = [];

    public override Hover GetHover()
    {
        StringBuilder builder = new();

        builder.AppendLine("```gsc");
        foreach (var overload in Source.Overloads)
        {
            if (Source.Overloads.Count > 1)
            {
                builder.AppendLine($"// Overload {Source.Overloads.IndexOf(overload) + 1}");
            }

            builder.Append($"{ClassSource.Name}::{Source.Name}(");

            bool first = true;
            foreach (ScrFunctionArg parameter in overload.Parameters)
            {
                AppendParameter(builder, parameter, ref first);
            }
            builder.AppendLine(")");
        }
        builder.AppendLine("```");

        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = Source.Documentation
            })
        };
    }
}

internal record ScrDependencySymbol(Range Range, string Path, string RawPath) : ISenseDefinition
{

    public Range Range { get; } = Range;

    public string SemanticTokenType { get; } = "string";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover()
    {
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = $"```gsc\n#using {RawPath}\n/* (script) \"{Path}\" */\n```"
            })
        };
    }
}
