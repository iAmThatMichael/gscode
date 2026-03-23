using GSCode.Parser.Configuration;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using GSCode.Parser.SPA;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GSCode.Parser.Data;

/// <summary>
/// A "dumb" implementation of a completions library, with naive heuristics for completion.
/// </summary>
public sealed class DocumentCompletionsLibrary(DocumentTokensLibrary tokens, string languageId, string? scriptPath = null)
{
    /// <summary>
    /// Library of tokens to quickly lookup a token at a given position.
    /// </summary>
    public DocumentTokensLibrary Tokens { get; } = tokens;

    /// <summary>
    /// Path to the current script file (for resolving relative paths).
    /// </summary>
    public string? ScriptPath { get; } = scriptPath;

    /// <summary>
    /// Definitions table to lookup function and class definitions.
    /// </summary>
    public DefinitionsTable? DefinitionsTable { get; set; }

    /// <summary>
    /// Macro definitions for completions.
    /// </summary>
    internal Dictionary<string, (Pre.MacroDefinition Definition, string? SourceDisplay)>? MacroDefinitions { get; set; }

    // Language ID (gsc or csc) for filtering file completions
    private readonly string _languageId = languageId;

    // Use shared API instance to avoid redundant allocations
    private readonly ScriptAnalyserData? _scriptAnalyserData = ScriptAnalyserData.GetShared(languageId);

    /// <summary>
    /// Pre-cached unique identifiers from the token stream for fast completions.
    /// </summary>
    private HashSet<string>? _cachedIdentifiers;

    public CompletionList GetCompletionsFromPosition(Position position)
    {
        Token? token = Tokens.Get(position);

        if (token is null)
        {
            Log.Warning("GetCompletionsFromPosition: No token found at position {Position}", position);
            return new CompletionList(isIncomplete: false);
        }

        Log.Debug("GetCompletionsFromPosition: Found token '{Lexeme}' type={Type} at position {Position}", 
            token.Lexeme, token.Type, position);

        // Log previous tokens for context
        Token? prevToken = token.Previous;
        int count = 0;
        while (prevToken != null && count < 5)
        {
            Log.Debug("  Previous token [{Index}]: '{Lexeme}' type={Type}", count, prevToken.Lexeme, prevToken.Type);
            prevToken = prevToken.Previous;
            count++;
        }

        // If the token is a LineBreak, we're likely typing on a new line
        // Try to get the next token which might be what we're actually typing
        if (token.Type == TokenType.LineBreak && token.Next != null && token.Next.Type != TokenType.Eof)
        {
            // Check if the next token is on the line we're requesting completion for
            if (token.Next.Range.Start.Line == position.Line)
            {
                Log.Information("Current token is LineBreak, using next token: '{Lexeme}' type={Type}", 
                    token.Next.Lexeme, token.Next.Type);
                token = token.Next;
            }
        }

        // Check if we're inside a function block
        bool isInsideFunctionBlock = IsInsideFunctionBlock(token);

        // Check if we're in a directive context (typing after # or on a directive token)
        bool isDirectiveContext = IsDirectiveContext(token, isInsideFunctionBlock);

        // Check if we're typing a path in a directive (e.g., #using scripts\)
        var directivePathContext = GetDirectivePathContext(token);

        // Look backwards to find the actual identifier being typed and check for namespace qualifier
        // This handles cases like:
        // 1. util::empt() - cursor on '(' or ')'
        // 2. util:: - cursor right after ::, no identifier yet
        string? namespaceQualifier = null;
        string filter = token.Lexeme;
        Token? identifierToken = null;

        // First, look backwards to skip whitespace and find the nearest significant token
        Token? prev = token.PreviousNonWhitespace();

        // Case 1: Current token is an identifier (e.g., typing "util::emp|")
        if (token.Type == TokenType.Identifier)
        {
            identifierToken = token;
            filter = token.Lexeme;
        }
        // Case 2: Previous token (after skipping whitespace) is :: - we're right after scope resolution
        // (e.g., "util::|" where cursor is after ::)
        else if (prev?.Type == TokenType.ScopeResolution)
        {
            // No identifier yet, empty filter
            filter = "";
            identifierToken = null;

            // Look for namespace before the ::
            Token? nsToken = prev.PreviousNonWhitespace();

            if (nsToken?.Type == TokenType.Identifier)
            {
                namespaceQualifier = nsToken.Lexeme;
                Log.Information("Detected namespace qualifier: {Namespace} (right after ::, no identifier yet)", 
                    namespaceQualifier);
            }
        }
        // Case 3: Previous token is an identifier - might be a function call with parens
        else if (prev?.Type == TokenType.Identifier)
        {
            identifierToken = prev;
            filter = prev.Lexeme;
        }
        // Case 4: Current token is OpenParen - look back further
        else if (token.Type == TokenType.OpenParen)
        {
            // prev is already set from above, look for identifier before (
            if (prev?.Type == TokenType.Identifier)
            {
                identifierToken = prev;
                filter = prev.Lexeme;
            }
        }

        // Now check if there's a namespace qualifier before the identifier
        // (only if we didn't already detect it in Case 2)
        if (identifierToken != null && namespaceQualifier == null)
        {
            Token? beforeIdent = identifierToken.PreviousNonWhitespace();

            if (beforeIdent?.Type == TokenType.ScopeResolution)
            {
                Token? nsToken = beforeIdent.PreviousNonWhitespace();

                if (nsToken?.Type == TokenType.Identifier)
                {
                    namespaceQualifier = nsToken.Lexeme;
                    Log.Information("Detected namespace qualifier: {Namespace} for identifier: {Identifier}", 
                        namespaceQualifier, identifierToken.Lexeme);
                }
            }
        }

        // Handle directive filter prefix
        if (isDirectiveContext && !filter.StartsWith("#"))
        {
            // We're in directive context but the current token doesn't have #
            // This means we're typing after the #, so prepend it for proper filtering
            filter = "#" + filter;
        }

        Log.Debug("Completion at position {Position}: token='{Lexeme}' type={Type}, filter='{Filter}', namespace={Namespace}, insideFunc={InsideFunc}, isDirective={IsDirective}, pathContext={PathContext}, prevToken={PrevType}", 
            position, token.Lexeme, token.Type, filter, namespaceQualifier ?? "(none)", isInsideFunctionBlock, isDirectiveContext, directivePathContext != null, token.Previous?.Type);

        // If NOT inside a function block and NOT typing a directive, return empty completions
        // (top-level code can only have directives and function definitions, not statements)
        if (!isInsideFunctionBlock && !isDirectiveContext)
        {
            Log.Debug("Returning empty completions (not in function and not directive context)");
            return new CompletionList(isIncomplete: false);
        }

        // For the moment, we'll just support Identifier completions.
        CompletionContext context = new()
        {
            Type = namespaceQualifier != null ? CompletionContextType.FunctionCall : CompletionContextType.GlobalScope,
            Filter = filter,
            Namespace = namespaceQualifier,
            IsDirectiveContext = isDirectiveContext,
            IsInsideFunctionBlock = isInsideFunctionBlock
        };
        Log.Debug("Created context: Type={Type}, Namespace={Namespace}, Filter={Filter}", 
            context.Type, context.Namespace ?? "(none)", context.Filter);

        List<CompletionItem> completions = new();

        // If we're typing a path in a directive, show path completions
        if (directivePathContext != null)
        {
            Log.Debug("Showing directive path completions for {DirectiveType}", directivePathContext.DirectiveType);
            // Set isIncomplete=true to hint that completions should be re-requested on any change
            return new CompletionList(GetDirectivePathCompletions(directivePathContext), isIncomplete: true);
        }

        // If in directive context, ONLY show directives
        if (isDirectiveContext)
        {
            Log.Debug("Showing directive completions only");
            HashSet<string> seenIdentifiers = new(StringComparer.OrdinalIgnoreCase);
            completions.AddRange(GetDirectiveCompletions(seenIdentifiers, token));
            return new CompletionList(completions, isIncomplete: false);
        }

        // Otherwise, show normal completions (only when inside a function block)
        Log.Debug("Showing normal completions (insideFunc={IsInsideFunc}, namespace={Namespace})", isInsideFunctionBlock, context.Namespace);
        switch (context.Type)
        {
            case CompletionContextType.GlobalScope:
                completions = GetGlobalScopeCompletions(context);
                Log.Debug("After GetGlobalScopeCompletions: {Count} completions", completions.Count);
                break;
            case CompletionContextType.FunctionCall:
                // Namespace-qualified function call (e.g., namespace::func)
                completions = GetNamespacedFunctionCompletions(context);
                Log.Debug("After GetNamespacedFunctionCompletions: {Count} completions", completions.Count);
                break;
        }

        // Get the completions from the definition.

        // Generate completions from identifiers that occur inside of the file, as well.
        // But skip this for namespace-qualified contexts (we only want namespace functions there)
        if (context.Type != CompletionContextType.FunctionCall || string.IsNullOrEmpty(context.Namespace))
        {
            var fileScopeCompletions = GetFileScopeCompletions(context);
            Log.Debug("GetFileScopeCompletions returned {Count} completions", fileScopeCompletions.Count);
            completions.AddRange(fileScopeCompletions);
        }

        Log.Debug("Returning total of {Count} completions", completions.Count);
        // return token.SenseDefinition?.GetCompletions();
        return new CompletionList(completions, isIncomplete: false);
    }

    private bool IsInsideFunctionBlock(Token token)
    {
        // Walk backwards through tokens to count braces
        // If we're inside more opening braces than closing braces, we're in a block
        int braceDepth = 0;
        Token? current = token;

        while (current != null && current.Type != TokenType.Sof)
        {
            if (current.Type == TokenType.OpenBrace)
            {
                braceDepth++;
            }
            else if (current.Type == TokenType.CloseBrace)
            {
                braceDepth--;
            }

            current = current.Previous;
        }

        // If braceDepth > 0, we're inside at least one block
        return braceDepth > 0;
    }

    private bool IsDirectiveContext(Token token, bool isInsideFunctionBlock)
    {
        // If inside a function block, Hash tokens are regular tokens, not directives
        // So we should NOT show directive completions
        if (isInsideFunctionBlock)
        {
            return false;
        }

        // Check if current token is a hash
        if (token.Type == TokenType.Hash)
        {
            Log.Information("IsDirectiveContext: Current token is Hash");
            return true;
        }

        // Check if current token is already a preprocessor directive token
        if (token.Type == TokenType.Using || token.Type == TokenType.Insert ||
            token.Type == TokenType.Namespace || token.Type == TokenType.Define ||
            token.Type == TokenType.Precache || token.Type == TokenType.UsingAnimTree ||
            token.Type == TokenType.PreIf || token.Type == TokenType.PreElIf ||
            token.Type == TokenType.PreElse || token.Type == TokenType.PreEndIf)
        {
            Log.Information("IsDirectiveContext: Current token is preprocessor directive type");
            return true;
        }

        // Check if previous non-whitespace token is a hash
        Token? prev = token.PreviousNonWhitespace();

        if (prev?.Type == TokenType.Hash)
        {
            Log.Information("IsDirectiveContext: Previous non-whitespace token is Hash");
            return true;
        }

        // As a fallback, check if ANY token on the current line is a Hash or preprocessor directive
        // This handles cases where the token chain is broken or stale
        int currentLine = token.Range.Start.Line;
        foreach (Token t in Tokens.GetAll())
        {
            // Only check tokens on the same line
            if (t.Range.Start.Line != currentLine)
            {
                if (t.Range.Start.Line > currentLine)
                {
                    break; // Past our line
                }
                continue; // Before our line
            }

            // Check if this token is a Hash or preprocessor directive
            if (t.Type == TokenType.Hash ||
                t.Type == TokenType.Using || t.Type == TokenType.Insert ||
                t.Type == TokenType.Namespace || t.Type == TokenType.Define ||
                t.Type == TokenType.Precache || t.Type == TokenType.UsingAnimTree ||
                t.Type == TokenType.PreIf || t.Type == TokenType.PreElIf ||
                t.Type == TokenType.PreElse || t.Type == TokenType.PreEndIf)
            {
                Log.Information("IsDirectiveContext: Found Hash or directive token on line {Line}: {TokenType}", currentLine, t.Type);
                return true;
            }
        }

        Log.Information("IsDirectiveContext: Not a directive context. Prev token: {PrevType}", prev?.Type);
        return false;
    }

    private List<CompletionItem> GetGlobalScopeCompletions(CompletionContext context)
    {
        // Get ALL API functions - let the client handle filtering based on what the user types
        List<ScrFunction> functions = _scriptAnalyserData?.GetApiFunctions(string.Empty) ?? [];

        List<CompletionItem> completions = new();

        // Only show API functions when inside a function block
        if (!context.IsInsideFunctionBlock)
        {
            Log.Information("Skipping global scope completions - not inside function block");
            return completions;
        }

        Log.Information("Found {Count} functions in global scope", functions.Count);

        foreach (ScrFunction function in functions)
        {
            completions.Add(CreateCompletionItem(function, namespaceAlreadyTyped: false));
        }

        return completions;
    }

    private List<CompletionItem> GetNamespacedFunctionCompletions(CompletionContext context)
    {
        List<CompletionItem> completions = new();
        HashSet<string> seenIdentifiers = new(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(context.Namespace))
        {
            Log.Warning("GetNamespacedFunctionCompletions: Namespace is null or empty");
            return completions;
        }

        if (DefinitionsTable is null)
        {
            Log.Warning("GetNamespacedFunctionCompletions: DefinitionsTable is null");
            return completions;
        }

        Log.Information("Looking for functions in namespace: {Namespace}, InternalSymbols count: {Count}", 
            context.Namespace, DefinitionsTable.InternalSymbols.Count);

        // Log all internal symbols for debugging
        foreach (var kvp in DefinitionsTable.InternalSymbols)
        {
            Log.Information("  InternalSymbol: Key='{Key}', Type={Type}", kvp.Key, kvp.Value.GetType().Name);
        }

        // When namespace is specified (e.g., util::), ONLY show functions from that namespace
        // Do NOT include API functions, macros, or other unrelated completions
        // Try both exact namespace match and the combined key
        string qualifiedPrefix = $"{context.Namespace}::";
        Log.Information("Looking for symbols starting with prefix: '{Prefix}'", qualifiedPrefix);

        // Check internal symbols with the namespace:: prefix
        int matchCount = 0;
        foreach (var kvp in DefinitionsTable.InternalSymbols)
        {
            if (kvp.Value is ScrFunction function)
            {
                // Check if this symbol key starts with the namespace prefix
                if (kvp.Key.StartsWith(qualifiedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    matchCount++;
                    // Extract the function name (after the ::)
                    string functionName = function.Name;

                    Log.Information("  Found matching function: Key='{Key}', Name='{Name}'", 
                        kvp.Key, functionName);

                    if (!seenIdentifiers.Contains(functionName))
                    {
                        completions.Add(CreateCompletionItem(function, namespaceAlreadyTyped: true));
                        seenIdentifiers.Add(functionName);
                        Log.Information("  Added function: {Name}", functionName);
                    }
                    else
                    {
                        Log.Information("  Skipped function: {Name} (already seen)", functionName);
                    }
                }
            }
        }

        // Also check local functions that match the namespace
        foreach (var functionTuple in DefinitionsTable.LocalScopedFunctions)
        {
            var function = functionTuple.Item1;
            // Check if function's namespace matches
            if (string.Equals(function.Namespace, context.Namespace, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(DefinitionsTable.CurrentNamespace, context.Namespace, StringComparison.OrdinalIgnoreCase))
            {
                if (!seenIdentifiers.Contains(function.Name))
                {
                    completions.Add(CreateCompletionItem(function, namespaceAlreadyTyped: true));
                    seenIdentifiers.Add(function.Name);
                    Log.Information("  Added local function: {Name} from namespace {Namespace}", function.Name, function.Namespace);
                }
            }
        }

        Log.Information("Found {MatchCount} namespace matches, returning {Count} namespaced functions for {Namespace}", 
            matchCount, completions.Count, context.Namespace);
        return completions;
    }

    private List<CompletionItem> GetFileScopeCompletions(CompletionContext context)
    {
        List<CompletionItem> completions = new();
        // Use case-sensitive comparison so "default" keyword and "DEFAULT" macro are both shown
        HashSet<string> seenIdentifiers = new(StringComparer.Ordinal);

        // Only show keywords and identifiers when inside a function block
        if (!context.IsInsideFunctionBlock)
        {
            return completions;
        }

        Log.Debug("GetFileScopeCompletions: Starting, token count: {Count}", Tokens.GetAll().Count());
        int macroCount = 0;
        int skippedApiCount = 0;
        int skippedPreprocCount = 0;
        int skippedFunctionCount = 0;

        // Add GSC/CSC keywords from shared definition
        foreach (string keyword in ScriptKeywords.All)
        {
            if (!seenIdentifiers.Contains(keyword))
            {
                completions.Add(new CompletionItem()
                {
                    Kind = CompletionItemKind.Keyword,
                    Label = keyword,
                    InsertText = keyword
                });
                seenIdentifiers.Add(keyword);
            }
        }

        // Add macros from MacroDefinitions dictionary
        if (MacroDefinitions is not null)
        {
            foreach (var kvp in MacroDefinitions)
            {
                string macroName = kvp.Key;
                var (macroDef, sourceDisplay) = kvp.Value;

                if (!seenIdentifiers.Contains(macroName))
                {
                    completions.Add(CreateMacroCompletionItem(macroName, macroDef, sourceDisplay));
                    seenIdentifiers.Add(macroName);
                    macroCount++;
                }
            }
        }

        // Add functions from the definitions table (both local and imported)
        if (DefinitionsTable is not null)
        {
            // Add locally defined functions
            foreach (var functionTuple in DefinitionsTable.LocalScopedFunctions)
            {
                var function = functionTuple.Item1;
                if (!seenIdentifiers.Contains(function.Name))
                {
                    completions.Add(CreateCompletionItem(function, namespaceAlreadyTyped: false));
                    seenIdentifiers.Add(function.Name);
                }
            }

            // Add internal symbols (functions from current namespace and dependencies)
            foreach (var kvp in DefinitionsTable.InternalSymbols)
            {
                if (kvp.Value is ScrFunction function)
                {
                    // Skip if already added or is an API function
                    if (seenIdentifiers.Contains(function.Name) || _scriptAnalyserData?.GetApiFunction(function.Name) is not null)
                    {
                        continue;
                    }

                    completions.Add(CreateCompletionItem(function, namespaceAlreadyTyped: false));
                    seenIdentifiers.Add(function.Name);
                }
            }
        }

        // Generate completions from identifiers that occur inside of the file
        foreach (Token token in Tokens.GetAll())
        {
            if (token.Type == TokenType.Identifier && !seenIdentifiers.Contains(token.Lexeme))
            {
                // Skip identifiers that are API function names to avoid duplicates
                if (_scriptAnalyserData?.GetApiFunction(token.Lexeme) is not null)
                {
                    skippedApiCount++;
                    continue;
                }

                // Skip identifiers that are from preprocessor expansion
                if (token.IsFromPreprocessor)
                {
                    skippedPreprocCount++;
                    continue;
                }

                // Check if this identifier is a function in the definitions table
                bool isFunction = false;
                if (DefinitionsTable is not null)
                {
                    // Check if it's a local function
                    isFunction = DefinitionsTable.LocalScopedFunctions.Any(f => 
                        string.Equals(f.Item1.Name, token.Lexeme, StringComparison.OrdinalIgnoreCase));

                    // Check if it's in internal symbols
                    if (!isFunction)
                    {
                        isFunction = DefinitionsTable.InternalSymbols.TryGetValue(token.Lexeme, out var symbol) 
                            && symbol is ScrFunction;
                    }
                }

                // If already added as a function, skip
                if (isFunction)
                {
                    skippedFunctionCount++;
                    continue;
                }

                completions.Add(new CompletionItem()
                {
                    Kind = CompletionItemKind.Variable,
                    Label = token.Lexeme,
                    InsertText = token.Lexeme
                });
                seenIdentifiers.Add(token.Lexeme);
            }
        }

        Log.Debug("GetFileScopeCompletions: Macros found={MacroCount}, Skipped: API={SkipApi}, Preprocessor={SkipPreproc}, Functions={SkipFunc}, Total completions={Total}",
            macroCount, skippedApiCount, skippedPreprocCount, skippedFunctionCount, completions.Count);

        return completions;
    }

    private CompletionItem CreateMacroCompletionItem(string macroName, Pre.MacroDefinition macroDef, string? sourceDisplay = null)
    {
        // Generate snippet-formatted insert text for macros
        string insertText = macroName;

        // Build detail showing the macro signature (like functions show their signature)
        string detail;
        if (macroDef.Parameters != null && macroDef.Parameters.Count > 0)
        {
            // Show like: DEFAULT(__var, __default)
            var paramNames = string.Join(", ", macroDef.Parameters.Select(p => p.Lexeme));
            detail = $"{macroName}({paramNames})";

            // Build insert text with snippet parameters
            insertText += "(";
            List<string> paramSnippets = new();
            int tabIndex = 1;

            foreach (var param in macroDef.Parameters)
            {
                paramSnippets.Add($"${{{tabIndex}:{param.Lexeme}}}");
                tabIndex++;
            }

            insertText += string.Join(", ", paramSnippets);
            insertText += ")$0";
        }
        else
        {
            // Show just the macro name for parameterless macros
            detail = macroName;
        }

        // Build documentation showing the macro definition
        string documentation = $"```gsc\n{macroDef.DefineSnippet}\n```";
        if (!string.IsNullOrEmpty(macroDef.Documentation))
        {
            documentation += $"\n\n{macroDef.Documentation}";
        }

        // Build labelDetails to show source file (like namespace badge for functions)
        CompletionItemLabelDetails? labelDetails = null;
        if (!string.IsNullOrEmpty(sourceDisplay))
        {
            labelDetails = new CompletionItemLabelDetails
            {
                Description = sourceDisplay  // Shows like "shared/shared.gsh" on the right
            };
        }

        return new CompletionItem()
        {
            Label = macroName,
            LabelDetails = labelDetails,
            Detail = detail,
            Documentation = new StringOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = documentation
            }),
            InsertText = insertText,
            InsertTextFormat = InsertTextFormat.Snippet,
            Kind = CompletionItemKind.Constant,
            SortText = "3_" + macroName.ToLowerInvariant()  // Sort after functions
        };
    }

    private CompletionItem CreateCompletionItem(ScrFunction function, bool namespaceAlreadyTyped = false)
    {
        // TODO: has been hacked to show first only, but we need to handle all overloads eventually.

        // Determine if we need to include namespace in insert text
        // Only include if it's a namespace function AND the namespace hasn't been typed yet
        bool includeNamespace = !namespaceAlreadyTyped && 
                                !function.Implicit && 
                                !string.IsNullOrEmpty(function.Namespace) && 
                                function.Namespace != "sys";

        // Build base insert text (with namespace if needed)
        string insertText = includeNamespace ? $"{function.Namespace}::{function.Name}" : function.Name;

        // Generate snippet-formatted parameters with tabstops
        // Only include mandatory parameters; skip optional parameters
        var mandatoryParams = function.Overloads.First().Parameters
            .Where(p => p.Mandatory.GetValueOrDefault(false))
            .ToList();

        if (mandatoryParams.Count > 0)
        {
            insertText += "(";

            // Create snippet-formatted parameter list with tabstops for mandatory parameters only
            List<string> paramSnippets = new List<string>();
            int tabIndex = 1;

            foreach (var param in function.Overloads.First().Parameters)
            {
                // Add mandatory parameters with tabstops
                if (param.Mandatory.GetValueOrDefault(false))
                {
                    paramSnippets.Add($"${{{tabIndex}:{param.Name ?? $"param{tabIndex}"}}}");
                    tabIndex++;
                }
                // Optional parameters are added with brackets
                else
                {
                    paramSnippets.Add($"${{{tabIndex}:[{param.Name ?? $"optionalParam{tabIndex}"}]}}");
                    tabIndex++;
                }
            }

            insertText += string.Join(", ", paramSnippets);

            // Place final tabstop inside parentheses (after mandatory params, before closing paren)
            // This allows user to add optional params if desired
            insertText += "$0)";
        }
        else
        {
            // No mandatory parameters - put cursor inside empty parentheses
            insertText += "($0)";
        }

        // Build detail and labelDetails for better differentiation
        string label;
        string detail;
        string? filterText = null;
        CompletionItemLabelDetails? labelDetails = null;
        CompletionItemKind kind;
        string sortPrefix;

        if (includeNamespace)
        {
            // Functions from other namespaces - show namespace::function in label and "function foo()" in detail
            label = $"{function.Namespace}::{function.Name}";
            detail = $"function {function.Name}()";
            filterText = function.Name;  // Filter only on function name, not the namespace prefix
            labelDetails = new CompletionItemLabelDetails
            {
                Description = function.Namespace  // Shows on right side like "util"
            };
            kind = CompletionItemKind.Method;  // Different icon than local functions
            sortPrefix = "2_";  // Sort after local/API functions
        }
        else if (namespaceAlreadyTyped)
        {
            // User already typed namespace::, so just show the function name
            label = function.Name;
            detail = $"function {function.Name}()";
            filterText = function.Name;  // Filter on function name only
            kind = CompletionItemKind.Function;
            sortPrefix = "0_";  // Sort first
        }
        else if (!string.IsNullOrEmpty(function.Description))
        {
            // API functions have descriptions - no badge needed
            label = function.Name;
            detail = function.Description;
            kind = CompletionItemKind.Function;
            sortPrefix = "0_";  // Sort first
        }
        else
        {
            // Local functions in current file
            label = function.Name;
            detail = $"function {function.Name}()";
            kind = CompletionItemKind.Function;
            sortPrefix = "1_";  // Sort after API but before namespace functions
        }

        // Append overload count to detail if needed
        if (function.Overloads.Count > 1)
        {
            string overloadSuffix = $"(+{function.Overloads.Count - 1} overload{(function.Overloads.Count > 2 ? "s" : "")})";
            detail = string.IsNullOrEmpty(detail) ? overloadSuffix : $"{detail} {overloadSuffix}";
        }

        return new CompletionItem()
        {
            Label = label,
            LabelDetails = labelDetails,
            Detail = detail,
            FilterText = filterText,  // Set FilterText to control VS Code's fuzzy matching
            Documentation = new StringOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = function.Documentation
            }),
            InsertText = insertText,
            InsertTextFormat = InsertTextFormat.Snippet,
            Kind = CompletionItemKind.Function,
            // Add sorting information to keep API functions organized
            SortText = function.Name.ToLowerInvariant(),
            // Add commit characters to automatically complete when typing these
            CommitCharacters = new Container<string>(new[] { "(", ")", ";" })
        };
    }

    /// <summary>
    /// Builds or returns the cached set of unique non-API identifiers from the token stream.
    /// </summary>
    private HashSet<string> GetCachedIdentifiers()
    {
        if (_cachedIdentifiers is not null) return _cachedIdentifiers;

        _cachedIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Token token in Tokens.GetAll())
        {
            if (token.Type == TokenType.Identifier && !_cachedIdentifiers.Contains(token.Lexeme))
            {
                // Skip identifiers that are API function names to avoid duplicates
                if (_scriptAnalyserData?.GetApiFunction(token.Lexeme) is not null)
                {
                    continue;
                }
                _cachedIdentifiers.Add(token.Lexeme);
            }
        }
        return _cachedIdentifiers;
    }

    private CompletionContext AnalyseCompletionContext(Token token, Position position)
    {
        var context = new CompletionContext { Position = position };

        TokenType currentType = token.Type;
        int tokenIdx = Tokens.IndexOf(token);
        // Use immediate adjacency (not skip-trivia) to preserve whitespace checks
        TokenType? previousType = tokenIdx > 0 ? Tokens.GetAt(tokenIdx - 1)?.Type : null;
        TokenType? previousPreviousType = tokenIdx > 1 ? Tokens.GetAt(tokenIdx - 2)?.Type : null;

        // // Check for member access (obj.__|)
        // if (previousType == TokenType.Dot)
        // {
        //     context.Type = CompletionContext.CompletionContextType.MemberAccess;

        //     // Determine object type (what's before the dot)
        //     var objectToken = token.Previous.Previous;
        //     if (objectToken?.Type == TokenType.Identifier)
        //     {
        //         context.ObjectType = DetermineTypeFromToken(objectToken);
        //     }
        // }
        // // Check for namespaced function (namespace::__|)
        // else if (previousType == TokenType.ScopeResolution &&
        //         previousPreviousType == TokenType.Identifier)
        // {
        //     context.Type = CompletionContext.CompletionContextType.FunctionCall;
        //     context.Namespace = token.Previous.Previous.Lexeme;
        // }
        // Check for global scope
        if (currentType == TokenType.Identifier ||
                previousType == TokenType.Whitespace)
        {
            context.Type = CompletionContextType.GlobalScope;
        }

        // If current token is an identifier, use it as filter
        if (currentType == TokenType.Identifier)
        {
            context.Filter = token.Lexeme;
        }

        return context;
    }

    private record DirectivePathContext(TokenType DirectiveType, string PartialPath);

    private record GameRootInfo(string PrimaryRoot, string? SecondaryRoot = null, string? ModName = null);

    private GameRootInfo? FindGameRoot(string startPath)
    {
        // Check if user has configured a custom raw path (will be used as secondary root if in a mod)
        string? customRawPath = CompletionConfiguration.CustomRawPath;
        bool hasValidCustomRawPath = false;

        if (!string.IsNullOrEmpty(customRawPath))
        {
            if (!Directory.Exists(customRawPath))
            {
                Log.Warning("FindGameRoot: Custom raw path is configured but does not exist: {Path}", customRawPath);
            }
            else
            {
                // Verify it actually contains a scripts folder
                string scriptsDir = Path.Combine(customRawPath, "scripts");
                if (!Directory.Exists(scriptsDir))
                {
                    Log.Warning("FindGameRoot: Custom raw path exists but does not contain a 'scripts' folder: {Path}. Path may be misconfigured.", customRawPath);
                }
                else
                {
                    hasValidCustomRawPath = true;
                    Log.Information("FindGameRoot: Found valid custom raw path from configuration: {Path}", customRawPath);
                }
            }
        }

        // Try to get the game path from environment variable (set by tooling)
        string? taGamePath = Environment.GetEnvironmentVariable("TA_GAME_PATH");
        if (!string.IsNullOrEmpty(taGamePath) && Directory.Exists(taGamePath))
        {
            Log.Information("FindGameRoot: Found TA_GAME_PATH environment variable: {Path}", taGamePath);

            // Check if the current file is in a mod folder
            // Mod structure: TA_GAME_PATH\mods\<modname>\scripts\...
            if (startPath.Contains("\\mods\\", StringComparison.OrdinalIgnoreCase))
            {
                // Extract the mod root path
                int modsIndex = startPath.IndexOf("\\mods\\", StringComparison.OrdinalIgnoreCase);
                if (modsIndex >= 0)
                {
                    // Find the mod name (the folder immediately after \mods\)
                    string afterMods = startPath.Substring(modsIndex + 6); // Skip "\mods\"
                    int nextSlash = afterMods.IndexOf('\\');

                    if (nextSlash > 0)
                    {
                        string modName = afterMods.Substring(0, nextSlash);
                        string modRoot = Path.Combine(taGamePath, "mods", modName);

                        if (Directory.Exists(modRoot))
                        {
                            // Determine secondary root: custom raw path takes priority over share\raw
                            string? secondaryRoot = null;

                            if (hasValidCustomRawPath)
                            {
                                secondaryRoot = customRawPath;
                                Log.Information("FindGameRoot: Current file is in mod '{ModName}', using mod root: {ModRoot} with custom secondary root: {CustomRaw}",
                                    modName, modRoot, customRawPath);
                            }
                            else
                            {
                                string tentativeShareRaw = Path.Combine(taGamePath, "share", "raw");
                                if (Directory.Exists(tentativeShareRaw))
                                {
                                    secondaryRoot = tentativeShareRaw;
                                    Log.Information("FindGameRoot: Current file is in mod '{ModName}', using mod root: {ModRoot} with secondary root: {ShareRaw}",
                                        modName, modRoot, tentativeShareRaw);
                                }
                                else
                                {
                                    Log.Information("FindGameRoot: Current file is in mod '{ModName}', using mod root: {ModRoot} (no share\\raw found)",
                                        modName, modRoot);
                                }
                            }

                            return new GameRootInfo(modRoot, secondaryRoot, modName);
                        }
                    }
                }
            }

            // Not in a mod folder - use custom raw path if available, otherwise share\raw
            if (hasValidCustomRawPath)
            {
                Log.Information("FindGameRoot: Using custom raw path as primary root: {Path}", customRawPath);
                return new GameRootInfo(customRawPath!);
            }

            // TA_GAME_PATH points to the game installation, but GSC scripts are in share\raw
            string shareRawPath = Path.Combine(taGamePath, "share", "raw");
            if (Directory.Exists(shareRawPath))
            {
                Log.Information("FindGameRoot: Using share\\raw subdirectory: {Path}", shareRawPath);
                return new GameRootInfo(shareRawPath);
            }

            // Check if there's a scripts folder directly in TA_GAME_PATH
            string scriptsPath = Path.Combine(taGamePath, "scripts");
            if (Directory.Exists(scriptsPath))
            {
                Log.Information("FindGameRoot: Using TA_GAME_PATH directly (contains scripts): {Path}", taGamePath);
                return new GameRootInfo(taGamePath);
            }

            Log.Warning("FindGameRoot: TA_GAME_PATH doesn't contain share\\raw or scripts, falling back to directory search");
        }

        // If custom raw path is set but TA_GAME_PATH is not, use custom raw path
        if (hasValidCustomRawPath)
        {
            Log.Information("FindGameRoot: TA_GAME_PATH not set, using custom raw path: {Path}", customRawPath);
            return new GameRootInfo(customRawPath!);
        }

        // Fallback: walk up the directory tree to find a directory containing a "scripts" folder
        Log.Information("FindGameRoot: TA_GAME_PATH not set or invalid, searching for scripts folder");
        string? current = startPath;

        while (current != null)
        {
            string scriptsDir = Path.Combine(current, "scripts");
            if (Directory.Exists(scriptsDir))
            {
                Log.Information("FindGameRoot: Found scripts folder at: {Path}", current);
                return new GameRootInfo(current);
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent == current) break; // Reached filesystem root
            current = parent;
        }

        Log.Warning("FindGameRoot: Could not find game root directory");
        return null;
    }

    private DirectivePathContext? GetDirectivePathContext(Token token)
    {
        // Walk backwards to find a directive token on the same line
        Token? current = token;
        TokenType? directiveType = null;
        int currentLine = token.Range.Start.Line;

        while (current != null && current.Range.Start.Line == currentLine)
        {
            if (current.Type == TokenType.Using || current.Type == TokenType.Insert)
            {
                directiveType = current.Type;
                break;
            }
            current = current.Previous;
        }

        if (directiveType == null)
            return null;

        // Collect all tokens after the directive to build the partial path
        Token? pathStart = current!.Next;
        StringBuilder pathBuilder = new();

        while (pathStart != null && pathStart.Range.Start.Line == currentLine && pathStart != token.Next)
        {
            Log.Information("GetDirectivePathContext: Processing token type={Type}, lexeme='{Lexeme}', isWhitespace={IsWhite}",
                pathStart.Type, pathStart.Lexeme, pathStart.IsWhitespacey());

            // Handle backslashes before the whitespace check (Backslash is considered whitespace in GSC)
            if (pathStart.Type == TokenType.Backslash || pathStart.Lexeme == "\\")
            {
                pathBuilder.Append("\\");
                Log.Information("GetDirectivePathContext: Added backslash");
            }
            else if (!pathStart.IsWhitespacey())
            {
                if (pathStart.Type == TokenType.String)
                {
                    pathBuilder.Append(pathStart.Lexeme.Trim('"'));
                    Log.Information("GetDirectivePathContext: Added string: '{Text}'", pathStart.Lexeme.Trim('"'));
                }
                else if (pathStart.Lexeme == "/")
                {
                    pathBuilder.Append("/");
                    Log.Information("GetDirectivePathContext: Added forward slash");
                }
                else if (pathStart.Type == TokenType.Identifier)
                {
                    pathBuilder.Append(pathStart.Lexeme);
                    Log.Information("GetDirectivePathContext: Added identifier: '{Text}'", pathStart.Lexeme);
                }
                else if (pathStart.Type == TokenType.Semicolon)
                {
                    Log.Information("GetDirectivePathContext: Encountered semicolon, stopping");
                    break;
                }
                else
                {
                    Log.Information("GetDirectivePathContext: Skipping unexpected token type {Type} with lexeme '{Lexeme}'",
                        pathStart.Type, pathStart.Lexeme);
                }
            }

            if (pathStart == token)
                break;

            pathStart = pathStart.Next;
        }

        string partialPath = pathBuilder.ToString().TrimEnd(';');
        Log.Information("GetDirectivePathContext: Built partialPath='{PartialPath}'", partialPath);

        if (!string.IsNullOrEmpty(partialPath) || token.Lexeme == "\\" || token.Lexeme == "/")
        {
            Log.Information("Detected directive path context: directive={DirectiveType}, partialPath='{PartialPath}'",
                directiveType, partialPath);
            return new DirectivePathContext(directiveType.Value, partialPath);
        }

        return null;
    }

    private List<CompletionItem> GetDirectiveCompletions(HashSet<string> seenIdentifiers, Token token)
    {
        List<CompletionItem> completions = new();

        // If # is already present, don't include it in InsertText to avoid ##using
        Token? prev = token.PreviousNonWhitespace();
        bool includeHash = prev?.Type != TokenType.Hash;

        Log.Information("GetDirectiveCompletions: token type={Type}, prev type={PrevType}, includeHash={IncludeHash}",
            token.Type, prev?.Type, includeHash);

        var directives = new[]
        {
            new {
                Label = "#using",
                InsertText = "#using ${1:scripts\\path\\to\\script};$0",
                Detail = "Import a script file",
                Documentation = "Imports functions and variables from another script file.\n\nExample: `#using scripts\\codescripts\\struct;`"
            },
            new {
                Label = "#insert",
                InsertText = "#insert ${1:scripts\\shared\\header.gsh};$0",
                Detail = "Insert a header file",
                Documentation = "Includes the contents of a header file into the current script.\n\nExample: `#insert scripts\\shared\\shared.gsh;`"
            },
            new {
                Label = "#namespace",
                InsertText = "#namespace ${1:namespace_name};$0",
                Detail = "Define a namespace",
                Documentation = "Defines the namespace for the current script.\n\nExample: `#namespace foo;`"
            },
            new {
                Label = "#define",
                InsertText = "#define ${1:CONSTANT_NAME} ${2:value}$0",
                Detail = "Define a constant or macro",
                Documentation = "Defines a preprocessor constant or macro.\n\nExamples:\n- `#define BAR 0`\n- `#define BAZ(_x) _x`"
            },
            new {
                Label = "#precache",
                InsertText = "#precache( ${1:\"string\"}, ${2:\"value\"} );$0",
                Detail = "Precache game assets",
                Documentation = "Precaches game assets for use in the script.\n\nExample: `#precache( \"string\", \"TEAM_GATHER_TEAM_STEALTH_ENTER\" );`"
            },
            new {
                Label = "#using_animtree",
                InsertText = "#using_animtree( ${1:\"animtree_name\"} );$0",
                Detail = "Specify animation tree",
                Documentation = "Specifies the animation tree to use for the script.\n\nExample: `#using_animtree( \"generic_human\" );`"
            },
            new {
                Label = "#if",
                InsertText = "#if ${1:condition}\n\t$0\n#endif",
                Detail = "Conditional compilation",
                Documentation = "Conditionally includes code based on a preprocessor condition.\n\nExample:\n```\n#if DEBUG\n    // debug code\n#endif\n```"
            },
            new {
                Label = "#elif",
                InsertText = "#elif ${1:condition}$0",
                Detail = "Else if condition",
                Documentation = "Alternative condition in a preprocessor conditional block."
            },
            new {
                Label = "#else",
                InsertText = "#else$0",
                Detail = "Else condition",
                Documentation = "Default branch in a preprocessor conditional block."
            },
            new {
                Label = "#endif",
                InsertText = "#endif$0",
                Detail = "End conditional block",
                Documentation = "Ends a preprocessor conditional block."
            }
        };

        foreach (var directive in directives)
        {
            if (!seenIdentifiers.Contains(directive.Label))
            {
                string insertText = includeHash ? directive.InsertText : directive.InsertText.Substring(1);

                completions.Add(new CompletionItem()
                {
                    Label = directive.Label,
                    Kind = CompletionItemKind.Keyword,
                    InsertText = insertText,
                    InsertTextFormat = InsertTextFormat.Snippet,
                    InsertTextMode = InsertTextMode.AdjustIndentation,
                    FilterText = directive.Label.TrimStart('#'),
                    Detail = directive.Detail,
                    Documentation = new StringOrMarkupContent(new MarkupContent()
                    {
                        Kind = MarkupKind.Markdown,
                        Value = directive.Documentation
                    }),
                    SortText = directive.Label
                });
                seenIdentifiers.Add(directive.Label);
            }
        }

        return completions;
    }

    private List<CompletionItem> GetDirectivePathCompletions(DirectivePathContext context)
    {
        List<CompletionItem> completions = new();

        Log.Information("GetDirectivePathCompletions: ScriptPath='{ScriptPath}', PartialPath='{PartialPath}'",
            ScriptPath, context.PartialPath);

        if (ScriptPath == null)
        {
            Log.Warning("GetDirectivePathCompletions: ScriptPath is null, cannot resolve paths");
            return completions;
        }

        try
        {
            // Convert URI path to local file system path
            string localPath = ScriptPath;
            if (Uri.TryCreate(ScriptPath, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            {
                localPath = uri.LocalPath;
            }
            else if (ScriptPath.StartsWith("/") && ScriptPath.Length > 2 && ScriptPath[2] == ':')
            {
                // Handle URI-style path like "/g:/..." -> "G:\..."
                localPath = ScriptPath.Substring(1).Replace('/', '\\');
            }

            Log.Information("GetDirectivePathCompletions: localPath='{LocalPath}'", localPath);

            string? scriptDir = Path.GetDirectoryName(localPath);
            if (scriptDir == null)
            {
                Log.Warning("GetDirectivePathCompletions: Could not get directory from localPath");
                return completions;
            }

            // In GSC/CSC, paths are relative to the game root (the directory containing "scripts")
            GameRootInfo? gameRootInfo = FindGameRoot(scriptDir);
            if (gameRootInfo == null)
            {
                Log.Warning("GetDirectivePathCompletions: Could not find game root, using script directory");
                gameRootInfo = new GameRootInfo(scriptDir);
            }

            Log.Information("GetDirectivePathCompletions: scriptDir='{ScriptDir}', primaryRoot='{PrimaryRoot}', secondaryRoot='{SecondaryRoot}'",
                scriptDir, gameRootInfo.PrimaryRoot, gameRootInfo.SecondaryRoot ?? "(none)");

            string searchPath = context.PartialPath.Replace('/', '\\');
            bool endsWithSeparator = searchPath.EndsWith("\\");
            searchPath = searchPath.TrimEnd('\\');

            List<string> searchRoots = new() { gameRootInfo.PrimaryRoot };
            if (!string.IsNullOrEmpty(gameRootInfo.SecondaryRoot))
                searchRoots.Add(gameRootInfo.SecondaryRoot);

            string fileFilter = "";
            List<string> searchDirs = new();

            foreach (string root in searchRoots)
            {
                string searchDir = root;

                if (!string.IsNullOrEmpty(searchPath))
                {
                    if (searchPath.Contains('\\'))
                    {
                        int lastSeparator = searchPath.LastIndexOf('\\');
                        string dirPart = searchPath.Substring(0, lastSeparator);
                        string lastPart = searchPath.Substring(lastSeparator + 1);

                        string tentativeDir = Path.Combine(root, dirPart);
                        Log.Information("GetDirectivePathCompletions: tentativeDir='{TentativeDir}', exists={Exists}",
                            tentativeDir, Directory.Exists(tentativeDir));

                        if (Directory.Exists(tentativeDir))
                        {
                            searchDir = tentativeDir;

                            if (endsWithSeparator && !string.IsNullOrEmpty(lastPart))
                            {
                                string fullDir = Path.Combine(searchDir, lastPart);
                                if (Directory.Exists(fullDir))
                                {
                                    searchDir = fullDir;
                                    fileFilter = "";
                                }
                                else
                                {
                                    fileFilter = lastPart;
                                }
                            }
                            else
                            {
                                fileFilter = lastPart;
                            }
                        }
                    }
                    else
                    {
                        string tentativeDir = Path.Combine(root, searchPath);
                        if (Directory.Exists(tentativeDir))
                        {
                            searchDir = tentativeDir;
                            fileFilter = endsWithSeparator ? "" : searchPath;
                        }
                        else
                        {
                            fileFilter = searchPath;
                        }
                    }
                }

                if (Directory.Exists(searchDir))
                    searchDirs.Add(searchDir);
            }

            Log.Information("GetDirectivePathCompletions: fileFilter='{FileFilter}', searchDirs={SearchDirs}",
                fileFilter, string.Join(", ", searchDirs));

            // Determine file extensions based on directive type
            string[] extensions;
            if (context.DirectiveType == TokenType.Using)
            {
                string ext = "." + _languageId.ToLowerInvariant();
                extensions = [ext];
                Log.Information("GetDirectivePathCompletions: #using directive, filtering to {Ext} files only", ext);
            }
            else
            {
                extensions = [".gsh"];
                Log.Information("GetDirectivePathCompletions: #insert directive, showing .gsh files");
            }

            HashSet<string> seenDirs = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);

            foreach (string searchDir in searchDirs)
            {
                Log.Information("GetDirectivePathCompletions: Searching in {SearchDir}", searchDir);

                var dirs = Directory.GetDirectories(searchDir);
                Log.Information("GetDirectivePathCompletions: Found {Count} directories in {SearchDir}", dirs.Length, searchDir);

                foreach (var dir in dirs)
                {
                    string dirName = Path.GetFileName(dir);
                    if ((string.IsNullOrEmpty(fileFilter) || dirName.StartsWith(fileFilter, StringComparison.OrdinalIgnoreCase))
                        && seenDirs.Add(dirName))
                    {
                        completions.Add(new CompletionItem()
                        {
                            Label = dirName,
                            Kind = CompletionItemKind.Folder,
                            InsertText = dirName + "\\\\",
                            Detail = "Folder",
                            SortText = "0_" + dirName
                        });
                    }
                }

                foreach (var ext in extensions)
                {
                    var files = Directory.GetFiles(searchDir, "*" + ext);
                    Log.Information("GetDirectivePathCompletions: Found {Count} {Ext} files in {SearchDir}", files.Length, ext, searchDir);

                    foreach (var file in files)
                    {
                        string fileName = Path.GetFileName(file);
                        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);

                        if ((string.IsNullOrEmpty(fileFilter) || fileName.StartsWith(fileFilter, StringComparison.OrdinalIgnoreCase) || fileNameWithoutExt.StartsWith(fileFilter, StringComparison.OrdinalIgnoreCase))
                            && seenFiles.Add(fileName))
                        {
                            string insertText = context.DirectiveType == TokenType.Insert
                                ? fileName + ";"
                                : fileNameWithoutExt + ";";

                            completions.Add(new CompletionItem()
                            {
                                Label = fileName,
                                Kind = CompletionItemKind.File,
                                InsertText = insertText,
                                Detail = $"Script file ({ext})",
                                SortText = "1_" + fileName
                            });
                        }
                    }
                }
            }

            Log.Information("GetDirectivePathCompletions: Returning {Count} completions (from {SearchDirCount} directories)",
                completions.Count, searchDirs.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to get directive path completions");
        }

        return completions;
    }
}


public record struct CompletionContext
{
    public CompletionContextType Type { get; set; }
    public string? Namespace { get; set; }
    public string? Filter { get; set; }
    public string? ObjectType { get; set; }
    public Position Position { get; set; }
    public bool IsDirectiveContext { get; set; }
    public bool IsInsideFunctionBlock { get; set; }
}

public enum CompletionContextType
{
    None,
    FunctionCall,
    MemberAccess,
    GlobalScope,
    TypeName
}