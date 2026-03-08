using GSCode.Parser.Configuration;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
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

    // Use shared API instance to avoid redundant allocations
    private readonly ScriptAnalyserData? _scriptAnalyserData = ScriptAnalyserData.GetShared(languageId);

    public CompletionList GetCompletionsFromPosition(Position position)
    {
        Token? token = Tokens.Get(position);

        if (token is null)
        {
            return new CompletionList(isIncomplete: false);
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

        // Build the filter string - include # prefix for directives
        string filter = token.Lexeme;
        if (isDirectiveContext && !filter.StartsWith("#"))
        {
            // We're in directive context but the current token doesn't have #
            // This means we're typing after the #, so prepend it for proper filtering
            filter = "#" + filter;
        }

        Log.Information("Completion at position {Position}: token='{Lexeme}' type={Type}, filter='{Filter}', insideFunc={InsideFunc}, isDirective={IsDirective}, pathContext={PathContext}, prevToken={PrevType}", 
            position, token.Lexeme, token.Type, filter, isInsideFunctionBlock, isDirectiveContext, directivePathContext != null, token.Previous?.Type);

        // If NOT inside a function block and NOT typing a directive, return empty completions
        // (top-level code can only have directives and function definitions, not statements)
        if (!isInsideFunctionBlock && !isDirectiveContext)
        {
            Log.Information("Returning empty completions (not in function and not directive context)");
            return new CompletionList(isIncomplete: false);
        }

        // For the moment, we'll just support Identifier completions.
        // CompletionContext context = AnalyseCompletionContext(token, position);
        CompletionContext context = new()
        {
            Type = CompletionContextType.GlobalScope,
            Filter = filter,
            IsDirectiveContext = isDirectiveContext,
            IsInsideFunctionBlock = isInsideFunctionBlock
        };
        List<CompletionItem> completions = new();

        // If we're typing a path in a directive, show path completions
        if (directivePathContext != null)
        {
            Log.Information("Showing directive path completions for {DirectiveType}", directivePathContext.DirectiveType);
            // Set isIncomplete=true to hint that completions should be re-requested on any change
            return new CompletionList(GetDirectivePathCompletions(directivePathContext), isIncomplete: true);
        }

        // If in directive context, ONLY show directives
        if (isDirectiveContext)
        {
            Log.Information("Showing directive completions only");
            HashSet<string> seenIdentifiers = new(StringComparer.OrdinalIgnoreCase);
            completions.AddRange(GetDirectiveCompletions(seenIdentifiers));
            return new CompletionList(completions, isIncomplete: false);
        }

        // Otherwise, show normal completions (only when inside a function block)
        Log.Information("Showing normal completions (insideFunc={IsInsideFunc})", isInsideFunctionBlock);
        switch (context.Type)
        {
            case CompletionContextType.GlobalScope:
                completions = GetGlobalScopeCompletions(context);
                Log.Information("After GetGlobalScopeCompletions: {Count} completions", completions.Count);
                break;
        }

        // Get the completions from the definition.

        // Generate completions from identifiers that occur inside of the file, as well.
        var fileScopeCompletions = GetFileScopeCompletions(context);
        Log.Information("GetFileScopeCompletions returned {Count} completions", fileScopeCompletions.Count);
        completions.AddRange(fileScopeCompletions);

        Log.Information("Returning total of {Count} completions", completions.Count);
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
        Token? prev = token.Previous;
        while (prev != null && prev.IsWhitespacey())
        {
            prev = prev.Previous;
        }

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
        List<ScrFunction> functions = _scriptAnalyserData?.GetApiFunctions(context.Filter) ?? [];

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
            completions.Add(CreateCompletionItem(function));
        }

        return completions;
    }

    private List<CompletionItem> GetFileScopeCompletions(CompletionContext context)
    {
        List<CompletionItem> completions = new();
        HashSet<string> seenIdentifiers = new(StringComparer.OrdinalIgnoreCase);

        // Only show keywords and identifiers when inside a function block
        if (!context.IsInsideFunctionBlock)
        {
            return completions;
        }

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

        // Generate completions from identifiers that occur inside of the file
        foreach (Token token in Tokens.GetAll())
        {
            if (token.Type == TokenType.Identifier && !seenIdentifiers.Contains(token.Lexeme))
            {
                // Skip identifiers that are API function names to avoid duplicates
                if (_scriptAnalyserData?.GetApiFunction(token.Lexeme) is not null)
                {
                    continue;
                }

                // Skip identifiers that are from preprocessor expansion
                if (token.IsFromPreprocessor)
                {
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

        return completions;
    }

    private CompletionItem CreateCompletionItem(ScrFunction function)
    {
        // TODO: has been hacked to show first only, but we need to handle all overloads eventually.
        // Generate snippet-formatted parameters with tabstops
        string insertText = function.Name;
        if (function.Overloads.First().Parameters.Count > 0)
        {
            insertText += "(";

            // Create snippet-formatted parameter list with tabstops
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
            insertText += ")";

            // Add a final tabstop after the closing parenthesis
            insertText += "$0";
        }
        else
        {
            insertText += "()$0";
        }

        return new CompletionItem()
        {
            Label = function.Name,
            Detail = function.Description,
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

    private List<CompletionItem> GetDirectiveCompletions(HashSet<string> seenIdentifiers)
    {
        List<CompletionItem> completions = new();

        // Define directives with their snippet patterns
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
                completions.Add(new CompletionItem()
                {
                    Label = directive.Label,
                    Kind = CompletionItemKind.Keyword,
                    InsertText = directive.InsertText,
                    InsertTextFormat = InsertTextFormat.Snippet,
                    FilterText = directive.Label.TrimStart('#'), // Allow filtering without the # prefix
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

    private record DirectivePathContext(TokenType DirectiveType, string PartialPath);

    private string? FindGameRoot(string startPath)
    {
        // First priority: Check if user has configured a custom raw path
        string? customRawPath = CompletionConfiguration.CustomRawPath;
        if (!string.IsNullOrEmpty(customRawPath) && Directory.Exists(customRawPath))
        {
            Log.Information("FindGameRoot: Using custom raw path from configuration: {Path}", customRawPath);
            return customRawPath;
        }

        // Second priority: Try to get the game path from environment variable (set by tooling)
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
                            Log.Information("FindGameRoot: Current file is in mod '{ModName}', using mod root: {Path}", 
                                modName, modRoot);
                            return modRoot;
                        }
                    }
                }
            }

            // TA_GAME_PATH points to the game installation, but GSC scripts are in share\raw
            // Check if share\raw exists
            string shareRawPath = Path.Combine(taGamePath, "share", "raw");
            if (Directory.Exists(shareRawPath))
            {
                Log.Information("FindGameRoot: Using share\\raw subdirectory: {Path}", shareRawPath);
                return shareRawPath;
            }

            // Check if there's a scripts folder directly in TA_GAME_PATH
            string scriptsPath = Path.Combine(taGamePath, "scripts");
            if (Directory.Exists(scriptsPath))
            {
                Log.Information("FindGameRoot: Using TA_GAME_PATH directly (contains scripts): {Path}", taGamePath);
                return taGamePath;
            }

            Log.Warning("FindGameRoot: TA_GAME_PATH doesn't contain share\\raw or scripts, falling back to directory search");
        }

        // Fallback: Walk up the directory tree to find a directory that contains "scripts" folder
        Log.Information("FindGameRoot: TA_GAME_PATH not set or invalid, searching for scripts folder");
        string? current = startPath;

        while (current != null)
        {
            // Check if this directory contains a "scripts" subdirectory
            string scriptsDir = Path.Combine(current, "scripts");
            if (Directory.Exists(scriptsDir))
            {
                Log.Information("FindGameRoot: Found scripts folder at: {Path}", current);
                return current;
            }

            // Move up one level
            string? parent = Path.GetDirectoryName(current);
            if (parent == current) // Reached root
            {
                break;
            }
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
            // Check if this is a directive that takes a path parameter
            if (current.Type == TokenType.Using || current.Type == TokenType.Insert)
            {
                directiveType = current.Type;
                break;
            }

            current = current.Previous;
        }

        if (directiveType == null)
        {
            return null;
        }

        // We're on a line with #using or #insert
        // Now we need to collect all tokens after the directive to build the partial path
        // Start from the directive and move forward
        Token? pathStart = current.Next;
        StringBuilder pathBuilder = new StringBuilder();

        while (pathStart != null && pathStart.Range.Start.Line == currentLine && pathStart != token.Next)
        {
            Log.Information("GetDirectivePathContext: Processing token type={Type}, lexeme='{Lexeme}', isWhitespace={IsWhite}", 
                pathStart.Type, pathStart.Lexeme, pathStart.IsWhitespacey());

            // Handle backslashes BEFORE the whitespace check (since Backslash is considered whitespace in GSC)
            if (pathStart.Type == TokenType.Backslash || pathStart.Lexeme == "\\")
            {
                pathBuilder.Append("\\");
                Log.Information("GetDirectivePathContext: Added backslash");
            }
            // Skip other whitespace
            else if (!pathStart.IsWhitespacey())
            {
                // Add token to path, handling strings specially
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
                    // Don't append semicolon, just break
                    Log.Information("GetDirectivePathContext: Encountered semicolon, stopping");
                    break;
                }
                else
                {
                    // Log unexpected token types for debugging
                    Log.Information("GetDirectivePathContext: Skipping unexpected token type {Type} with lexeme '{Lexeme}'", 
                        pathStart.Type, pathStart.Lexeme);
                }
            }

            if (pathStart == token)
            {
                break;
            }

            pathStart = pathStart.Next;
        }

        string partialPath = pathBuilder.ToString().TrimEnd(';');

        Log.Information("GetDirectivePathContext: Built partialPath='{PartialPath}'", partialPath);

        // Only return a path context if we have some path content or just typed a separator
        if (!string.IsNullOrEmpty(partialPath) || token.Lexeme == "\\" || token.Lexeme == "/")
        {
            Log.Information("Detected directive path context: directive={DirectiveType}, partialPath='{PartialPath}'", 
                directiveType, partialPath);
            return new DirectivePathContext(directiveType.Value, partialPath);
        }

        return null;
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

            // Get the directory of the current script
            string? scriptDir = Path.GetDirectoryName(localPath);
            if (scriptDir == null)
            {
                Log.Warning("GetDirectivePathCompletions: Could not get directory from localPath");
                return completions;
            }

            // In GSC/CSC, paths are relative to the game root (typically the directory containing "scripts")
            // Walk up the directory tree to find the root
            string? gameRoot = FindGameRoot(scriptDir);
            if (gameRoot == null)
            {
                Log.Warning("GetDirectivePathCompletions: Could not find game root, using script directory");
                gameRoot = scriptDir;
            }

            Log.Information("GetDirectivePathCompletions: scriptDir='{ScriptDir}', gameRoot='{GameRoot}'", 
                scriptDir, gameRoot);

            // Parse the partial path to determine what directory to search
            string searchPath = context.PartialPath.Replace('/', '\\');
            bool endsWithSeparator = searchPath.EndsWith("\\");
            searchPath = searchPath.TrimEnd('\\');

            string searchDir = gameRoot;
            string fileFilter = "";

            if (!string.IsNullOrEmpty(searchPath))
            {
                if (searchPath.Contains('\\'))
                {
                    // Path contains directory separators (e.g., "scripts\shared" or "scripts\shared\")
                    int lastSeparator = searchPath.LastIndexOf('\\');
                    string dirPart = searchPath.Substring(0, lastSeparator);
                    string lastPart = searchPath.Substring(lastSeparator + 1);

                    // Try to resolve the directory relative to game root
                    string tentativeDir = Path.Combine(gameRoot, dirPart);
                    Log.Information("GetDirectivePathCompletions: tentativeDir='{TentativeDir}', exists={Exists}", 
                        tentativeDir, Directory.Exists(tentativeDir));

                    if (Directory.Exists(tentativeDir))
                    {
                        searchDir = tentativeDir;

                        // If original path ended with \, the last part is a directory, not a filter
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
                    // No directory separators - check if it's a directory or a file filter
                    string tentativeDir = Path.Combine(gameRoot, searchPath);
                    if (Directory.Exists(tentativeDir))
                    {
                        // It's a directory that exists
                        searchDir = tentativeDir;
                        fileFilter = endsWithSeparator ? "" : searchPath;
                    }
                    else
                    {
                        // It's a file filter in the game root
                        fileFilter = searchPath;
                    }
                }
            }

            Log.Information("GetDirectivePathCompletions: searchDir='{SearchDir}', fileFilter='{FileFilter}'", 
                searchDir, fileFilter);

            // Determine file extensions based on directive type
            string[] extensions = context.DirectiveType == TokenType.Using
                ? new[] { ".gsc", ".csc" }
                : new[] { ".gsh" }; // Insert

            // Add directories
            if (Directory.Exists(searchDir))
            {
                var dirs = Directory.GetDirectories(searchDir);
                Log.Information("GetDirectivePathCompletions: Found {Count} directories in {SearchDir}", 
                    dirs.Length, searchDir);

                foreach (var dir in dirs)
                {
                    string dirName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(fileFilter) || dirName.StartsWith(fileFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        completions.Add(new CompletionItem()
                        {
                            Label = dirName,
                            Kind = CompletionItemKind.Folder,
                            InsertText = dirName + "\\\\",
                            Detail = "Folder",
                            SortText = "0_" + dirName // Folders first
                        });
                    }
                }

                // Add files with appropriate extensions
                foreach (var ext in extensions)
                {
                    var files = Directory.GetFiles(searchDir, "*" + ext);
                    Log.Information("GetDirectivePathCompletions: Found {Count} {Ext} files in {SearchDir}", 
                        files.Length, ext, searchDir);

                    foreach (var file in files)
                    {
                        string fileName = Path.GetFileName(file);
                        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);

                        if (string.IsNullOrEmpty(fileFilter) || fileName.StartsWith(fileFilter, StringComparison.OrdinalIgnoreCase) || fileNameWithoutExt.StartsWith(fileFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            completions.Add(new CompletionItem()
                            {
                                Label = fileName,
                                Kind = CompletionItemKind.File,
                                InsertText = fileNameWithoutExt, // Insert without extension
                                Detail = $"Script file ({ext})",
                                SortText = "1_" + fileName // Files after folders
                            });
                        }
                    }
                }

                Log.Information("GetDirectivePathCompletions: Returning {Count} completions", completions.Count);
            }
            else
            {
                Log.Warning("GetDirectivePathCompletions: searchDir does not exist: {SearchDir}", searchDir);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to get directive path completions");
        }

        return completions;
    }

    private CompletionContext AnalyseCompletionContext(Token token, Position position)
    {
        var context = new CompletionContext { Position = position };

        TokenType currentType = token.Type;
        TokenType? previousType = token.Previous?.Type;
        TokenType? previousPreviousType = token.Previous?.Previous?.Type;

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