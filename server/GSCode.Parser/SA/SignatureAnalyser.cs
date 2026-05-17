using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using Serilog;
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
    private bool _inDevBlock = false;

    public void Analyse()
    {
        foreach (AstNode dep in RootNode.UsingNodes)
            if (dep is UsingNode depNode) AnalyseUsing(depNode);

        foreach (AstNode defn in RootNode.ScriptDefns)
        {
            try
            {
                AnalyseScriptDefinition(defn);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[SA] Exception analysing {NodeType} in {Script}", defn.GetType().Name, Sense.ScriptPath);
                throw;
            }
        }
    }

    private void AnalyseScriptDefinition(AstNode node)
    {
        switch (node)
        {
            case FunDefnNode fn:       AnalyseFunction(fn);   break;
            case NamespaceNode ns:     AnalyseNamespace(ns);  break;
            case ClassDefnNode cls:    AnalyseClass(cls);     break;
            case DefnDevBlockNode dev: AnalyseDevBlock(dev);  break;
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
            switch (child)
            {
                case FunDefnNode fn:        AnalyseClassFunction(scrClass, fn);    break;
                case MemberDeclNode member: AnalyseClassMember(scrClass, member);  break;
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
        string? doc = docCommentToken is null ? null : DocCommentHelper.Sanitize(docCommentToken.Lexeme);

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

        var flags = BuildFunctionFlags(functionDefn, function.Private);
        RegisterFunctionInNamespace(DefinitionsTable.CurrentNamespace, name, nameToken.TokenRange, function, doc, flags);
        // Also register under the class name as its own qualifier so ClassName::Method() resolves
        RegisterFunctionInNamespace(scrClass.Name, name, nameToken.TokenRange, function, doc, flags);

        Sense.AddSenseToken(nameToken, new ScrMethodSymbol(nameToken, function, scrClass));
        RegisterParameterSenseTokens(parameters, functionDefn.Parameters);
    }

    private void AnalyseClassMember(ScrClass scrClass, MemberDeclNode memberDecl)
    {
        if (memberDecl.NameToken is not Token nameToken)
        {
            return;
        }

        // Extract raw doc comment token and sanitize it
        Token? docCommentToken = FindDocCommentTokenBefore(nameToken);
        string? doc = docCommentToken is null ? null : DocCommentHelper.Sanitize(docCommentToken.Lexeme);

        ScrMember member = new()
        {
            Name = memberDecl.NameToken?.Lexeme ?? "",
            Description = null,
            DocComment = doc
        };

        scrClass.Members.Add(member);
        Sense.AddSenseToken(nameToken, new ScrClassMemberSymbol(nameToken, member, scrClass));
    }

    public void AnalyseUsing(UsingNode dependencyNode)
    {
        string? dependencyPath = Sense.ResolveUsingPath(dependencyNode.Path, dependencyNode.Range);
        if (dependencyPath is null)
        {
            return;
        }

        Sense.AddSenseToken(dependencyNode.FirstPathToken, new ScrDependencySymbol(dependencyNode.Range, dependencyPath, dependencyNode.Path));

        // Add the using path to the list
        DefinitionsTable.AddUsingPath(dependencyPath);
    }

    public void AnalyseNamespace(NamespaceNode namespaceNode)
    {
        // Change the namespace at this point and onwards
        DefinitionsTable.CurrentNamespace = namespaceNode.NamespaceIdentifier;
    }

    public void AnalyseDevBlock(DefnDevBlockNode devBlockNode)
    {
        bool wasInDevBlock = _inDevBlock;
        _inDevBlock = true;

        foreach (AstNode node in devBlockNode.Definitions)
            AnalyseScriptDefinition(node);

        _inDevBlock = wasInDevBlock;
    }

    public void AnalyseFunction(FunDefnNode functionDefn)
    {
        // Get the name of the function - if it's unnamed then it's one that was produced in recovery. No use to us.
        if (functionDefn.Name is not Token nameToken)
            return;

        string name = nameToken.Lexeme;

        // Extract raw doc comment token
        Token? docCommentToken = FindDocCommentTokenBefore(nameToken);

        // Parse mandatory parameters from raw text
        var mandatoryParams = ExtractMandatoryParametersFromRaw(docCommentToken?.Lexeme);

        // Store the SANITIZED (but not markdown-formatted) doc comment
        string? docComment = docCommentToken is null ? null : DocCommentHelper.Sanitize(docCommentToken.Lexeme);

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
            DocComment = docComment,
            InDevBlock = _inDevBlock
        };

        // Produce a definition for our function
        DefinitionsTable.AddFunction(function, functionDefn);

        var flags = BuildFunctionFlags(functionDefn, function.Private);
        RegisterFunctionInNamespace(DefinitionsTable.CurrentNamespace, name, nameToken.TokenRange, function, function.DocComment, flags);

        Sense.AddSenseToken(nameToken, new ScrFunctionSymbol(nameToken, function));
        RegisterParameterSenseTokens(parameters, functionDefn.Parameters);
    }

    private static List<ScrFunctionArg>? GetParametersAsRecord(IEnumerable<ScrParameter> parameters) =>
        parameters.Select(p => new ScrFunctionArg
        {
            Name = p.Name,
            Description = null,
            Type = null,
            Mandatory = p.Mandatory,
            Default = null
        }).ToList();

    private List<ScrParameter> AnalyseFunctionParameters(ParamListNode parameters, HashSet<string> mandatoryParams) =>
        parameters.Parameters
            .Where(p => p.Name is not null)
            .Select(p => new ScrParameter(
                p.Name!.Lexeme,
                p.Name,
                p.Name.Range,
                p.ByRef,
                p.Default,
                Mandatory: mandatoryParams.Contains(p.Name!.Lexeme)))
            .ToList();

    private static List<string> BuildFunctionFlags(FunDefnNode functionDefn, bool isPrivate)
    {
        var flags = new List<string>(2);
        if (isPrivate) flags.Add("private");
        if (functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Autoexec)) flags.Add("autoexec");
        return flags;
    }

    private void RegisterFunctionInNamespace(string ns, string name, TokenRange nameTokenRange,
        ScrFunction function, string? doc, IEnumerable<string> flags)
    {
        DefinitionsTable.AddFunctionLocation(ns, name, Sense.ScriptPath, nameTokenRange);
        DefinitionsTable.RecordFunctionParameters(ns, name, (function.Overloads[0].Parameters ?? []).Select(a => a.Name));
        DefinitionsTable.RecordFunctionFlags(ns, name, flags);
        DefinitionsTable.RecordFunctionDoc(ns, name, doc);
    }

    private void RegisterParameterSenseTokens(IEnumerable<ScrParameter> parameters, ParamListNode paramList)
    {
        int i = 0;
        foreach (ScrParameter parameter in parameters)
        {
            Sense.AddSenseToken(parameter.Source, new ScrParameterSymbol(parameter, paramList.Parameters.ElementAtOrDefault(i)));
            i++;
        }
    }

    private Token? FindDocCommentTokenBefore(Token nameToken)
    {
        DocumentTokensLibrary tokens = Sense.Tokens;
        int idx = tokens.IndexOf(nameToken);
        if (idx < 0) return null;

        // Walk backwards to the start of the current line (first LineBreak before the name token)
        int lineStart = idx;
        for (int i = idx - 1; i >= 0; i--)
        {
            Token? t = tokens.GetAt(i);
            if (t == null) break;
            if (t.Type == TokenType.LineBreak)
            {
                lineStart = i;
                break;
            }
        }

        // From the line start, scan backwards (up to ~50 tokens) for a DocComment
        for (int i = lineStart - 1, count = 0; i >= 0 && count < 50; i--, count++)
        {
            Token? t = tokens.GetAt(i);
            if (t == null) break;
            if (t.Type == TokenType.DocComment) return t;
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
}
