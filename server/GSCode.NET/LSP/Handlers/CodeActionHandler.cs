using GSCode.Data;
using GSCode.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.IO;
using System.Linq;
using System.Text;

namespace GSCode.NET.LSP.Handlers;

internal sealed class CodeActionHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : CodeActionHandlerBase
{
    private readonly ScriptManager _scriptManager = scriptManager;
    private readonly TextDocumentSelector _documentSelector = documentSelector;

    protected override CodeActionRegistrationOptions CreateRegistrationOptions(
        CodeActionCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = _documentSelector,
            ResolveProvider = true,
            CodeActionKinds = new Container<CodeActionKind>(
                CodeActionKind.QuickFix,
                CodeActionKind.SourceOrganizeImports)
        };

    public override Task<CodeAction> Handle(CodeAction request, CancellationToken cancellationToken)
        => Task.FromResult(request);

    public override async Task<CommandOrCodeActionContainer?> Handle(
        CodeActionParams request, CancellationToken cancellationToken)
    {
        var actions = await GetCodeActionsAsync(request, cancellationToken);
        return new CommandOrCodeActionContainer(actions.Select(a => new CommandOrCodeAction(a)));
    }

    private async Task<CodeAction[]> GetCodeActionsAsync(
        CodeActionParams request, CancellationToken cancellationToken)
    {
        var actions = new List<CodeAction>();

        // Pre-fetch cached document text once — only needed by a subset of fixes
        _scriptManager.TryGetCachedContent(request.TextDocument.Uri.ToUri(), out string content);

        foreach (Diagnostic diagnostic in request.Context.Diagnostics)
        {
            if (!diagnostic.Code.HasValue || !diagnostic.Code.Value.IsLong)
                continue;

            GSCErrorCodes errorCode = (GSCErrorCodes)(int)diagnostic.Code.Value.Long;

            CodeAction? action = errorCode switch
            {
                // Remove the entire #using line — file is unreferenced
                GSCErrorCodes.UnusedUsing =>
                    CreateDeleteLineAction(request.TextDocument, diagnostic, "Remove unused #using directive"),

                // Remove the entire #using line — file could not be located
                GSCErrorCodes.MissingUsingFile =>
                    CreateDeleteLineAction(request.TextDocument, diagnostic, "Remove unresolvable #using directive"),

                // Remove the variable declaration line — variable is declared but never read
                GSCErrorCodes.UnusedVariable =>
                    CreateDeleteLineAction(request.TextDocument, diagnostic, "Remove unused variable declaration"),

                // Remove duplicate macro definition line
                GSCErrorCodes.DuplicateMacroDefinition =>
                    CreateDeleteLineAction(request.TextDocument, diagnostic, "Remove duplicate macro definition", isPreferred: true),

                // Remove the duplicate case label line
                GSCErrorCodes.DuplicateCaseLabel =>
                    CreateDeleteLineAction(request.TextDocument, diagnostic, "Remove duplicate 'case' label", isPreferred: true),

                // Remove the extra default label line
                GSCErrorCodes.MultipleDefaultLabels =>
                    CreateDeleteLineAction(request.TextDocument, diagnostic, "Remove duplicate 'default' label", isPreferred: true),

                // Insert a semicolon at the exact position the parser expected one
                GSCErrorCodes.ExpectedSemiColon =>
                    CreateInsertAction(request.TextDocument, diagnostic, diagnostic.Range.End, ";", "Insert missing ';'", isPreferred: true),

                // Prepend the ampersand operator to turn the bare name into a function pointer
                GSCErrorCodes.StoreFunctionAsPointer =>
                    CreateInsertAction(request.TextDocument, diagnostic, diagnostic.Range.Start, "&", "Add '&' function pointer operator", isPreferred: true),

                // Prefix the unused parameter name with _ to signal intentional non-use
                GSCErrorCodes.UnusedParameter =>
                    CreateInsertAction(request.TextDocument, diagnostic, diagnostic.Range.Start, "_", "Prefix parameter with '_'"),

                // Append () so the macro is invoked rather than referenced bare
                GSCErrorCodes.MissingMacroParameterList =>
                    CreateInsertAction(request.TextDocument, diagnostic, diagnostic.Range.End, "()", "Add missing '()' to macro invocation", isPreferred: true),

                // Erase the range the SPA tagged as unreachable
                GSCErrorCodes.UnreachableCodeDetected =>
                    CreateDeleteRangeAction(request.TextDocument, diagnostic, diagnostic.Range, "Remove unreachable code", isPreferred: true),

                // Erase the duplicate modifier token
                GSCErrorCodes.DuplicateModifier =>
                    CreateDeleteRangeAction(request.TextDocument, diagnostic, diagnostic.Range, "Remove duplicate modifier", isPreferred: true),

                // Erase the unreachable case block
                GSCErrorCodes.UnreachableCase =>
                    CreateDeleteRangeAction(request.TextDocument, diagnostic, diagnostic.Range, "Remove unreachable 'case'", isPreferred: true),

                // Replace integer literal 0/1 with the equivalent boolean keyword
                GSCErrorCodes.PreferBooleanLiteral =>
                    CreatePreferBooleanLiteralAction(request.TextDocument, diagnostic, content),

                // Convert [ a, b, c ] initialiser syntax to array(a, b, c)
                GSCErrorCodes.SquareBracketInitialisationNotSupported =>
                    CreateSquareBracketToArrayAction(request.TextDocument, diagnostic, content),

                // Append a matching /* endregion */ at the end of the document
                GSCErrorCodes.UnterminatedRegion =>
                    CreateUnterminatedRegionAction(request.TextDocument, diagnostic, content),

                // Append #endif at the end of the document to close the open directive
                GSCErrorCodes.UnterminatedPreprocessorDirective =>
                    CreateAppendLineAction(request.TextDocument, diagnostic, "#endif", content, isPreferred: true),

                // Move the misplaced #using directive to the top of the file
                GSCErrorCodes.UnexpectedUsing =>
                    CreateMoveUsingToTopAction(request.TextDocument, diagnostic, content),

                // Remove 'thread' from a call whose result is consumed.
                // The diagnostic range is a PrefixExprNode [thread_token.start → operand.end],
                // so Range.Start is always the first character of the 'thread' keyword.
                // Each diagnostic on the same line has its own distinct range, so each
                // code action independently removes exactly one 'thread' keyword.
                GSCErrorCodes.ConsumedThreadedCallResult =>
                    CreateRemoveThreadKeywordAction(request.TextDocument, diagnostic, content),

                // Generate a stub function definition at the end of the file.
                // The diagnostic range covers the function name identifier token, so
                // GetTextInRange gives the name without any message parsing.
                GSCErrorCodes.FunctionDoesNotExist =>
                    CreateGenerateFunctionStubAction(request.TextDocument, diagnostic, content),

                _ => null
            };

            if (action is not null)
            {
                actions.Add(action);
            }

            // MissingUsingFile gets a second option: create the file at its expected path
            // in addition to the existing "remove #using" quick fix.
            if (errorCode == GSCErrorCodes.MissingUsingFile)
            {
                CodeAction? createAction = TryCreateMissingFileAction(
                    request.TextDocument, diagnostic, content);

                if (createAction is not null)
                {
                    actions.Add(createAction);
                }
            }

            // UnknownNamespace: offer to add a #using directive for each file that
            // exports symbols into the missing namespace. When multiple files define
            // the same namespace, one code action per file is emitted so the user can
            // pick the correct dependency.
            if (errorCode == GSCErrorCodes.UnknownNamespace)
            {
                var usingActions = CreateAddUsingForNamespaceActions(
                    request.TextDocument, diagnostic, content);

                foreach (var usingAction in usingActions)
                {
                    actions.Add(usingAction);
                }
            }
        }

        // Source action: remove all unused #using directives in the file at once.
        // Only emitted when there are at least two so it is meaningfully distinct from
        // the per-diagnostic quick fix already offered above.
        CodeAction? bulkAction = await TryCreateRemoveAllUnusedUsingsActionAsync(
            request.TextDocument, cancellationToken);

        if (bulkAction is not null)
        {
            actions.Add(bulkAction);
        }

        Serilog.Log.Debug("CodeActionHandler produced {Count} action(s) for {Uri}", actions.Count, request.TextDocument.Uri);
        return actions.ToArray();
    }

    // -------------------------------------------------------------------------
    // Fix factories
    // -------------------------------------------------------------------------

    /// <summary>
    /// Deletes the entire line that contains the diagnostic (including the trailing newline).
    /// Suitable for whole-line constructs such as <c>#using</c> directives and simple
    /// single-statement variable declarations.
    /// </summary>
    private static CodeAction CreateDeleteLineAction(
        TextDocumentIdentifier document, Diagnostic diagnostic, string title,
        bool isPreferred = false)
    {
        int line = diagnostic.Range.Start.Line;

        return new CodeAction
        {
            Title = title,
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] =
                    [
                        new TextEdit
                        {
                            // (line, 0) → (line+1, 0) removes the line and its newline
                            Range = new Range { Start = new Position { Line = line, Character = 0 }, End = new Position { Line = line + 1, Character = 0 } },
                            NewText = string.Empty
                        }
                    ]
                }
            }
        };
    }

    /// <summary>
    /// Inserts <paramref name="text"/> at <paramref name="position"/> with no range deletion.
    /// </summary>
    private static CodeAction CreateInsertAction(
        TextDocumentIdentifier document, Diagnostic diagnostic, Position position,
        string text, string title, bool isPreferred = false)
    {
        return new CodeAction
        {
            Title = title,
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] =
                    [
                        new TextEdit
                        {
                            Range = new Range { Start = position, End = position },
                            NewText = text
                        }
                    ]
                }
            }
        };
    }

    /// <summary>
    /// Replaces the given <paramref name="range"/> with an empty string.
    /// Suitable for removing a single token or an already-isolated block.
    /// </summary>
    private static CodeAction CreateDeleteRangeAction(
        TextDocumentIdentifier document, Diagnostic diagnostic, Range range,
        string title, bool isPreferred = false)
    {
        return new CodeAction
        {
            Title = title,
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] =
                    [
                        new TextEdit
                        {
                            Range = range,
                            NewText = string.Empty
                        }
                    ]
                }
            }
        };
    }

    /// <summary>
    /// Reads the integer literal (<c>0</c> or <c>1</c>) at the diagnostic range and
    /// replaces it with the equivalent boolean keyword (<c>false</c> or <c>true</c>).
    /// </summary>
    private static CodeAction? CreatePreferBooleanLiteralAction(
        TextDocumentIdentifier document, Diagnostic diagnostic, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        string? literal = GetTextInRange(content, diagnostic.Range);
        if (literal is null)
        {
            return null;
        }

        string replacement = literal.Trim() == "0" ? "false" : "true";

        return new CodeAction
        {
            Title = $"Replace with '{replacement}'",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] =
                    [
                        new TextEdit
                        {
                            Range = diagnostic.Range,
                            NewText = replacement
                        }
                    ]
                }
            }
        };
    }

    /// <summary>
    /// Converts a <c>[ a, b, c ]</c> collection initialiser to <c>array(a, b, c)</c>
    /// by extracting the content between the brackets and wrapping it with <c>array(…)</c>.
    /// </summary>
    private static CodeAction? CreateSquareBracketToArrayAction(
        TextDocumentIdentifier document, Diagnostic diagnostic, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        string? bracketExpr = GetTextInRange(content, diagnostic.Range);
        if (bracketExpr is null || bracketExpr.Length < 2)
        {
            return null;
        }

        // Strip surrounding [ and ] then rewrap as array(...)
        string inner = bracketExpr[1..^1];
        string replacement = $"array({inner})";

        return new CodeAction
        {
            Title = "Replace with 'array()' initialiser",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] =
                    [
                        new TextEdit
                        {
                            Range = diagnostic.Range,
                            NewText = replacement
                        }
                    ]
                }
            }
        };
    }

    /// <summary>
    /// Appends a <c>/* endregion */</c> comment on a new line at the very end of the
    /// document to close the region that was opened without a matching end marker.
    /// </summary>
    private static CodeAction? CreateUnterminatedRegionAction(
        TextDocumentIdentifier document, Diagnostic diagnostic, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        // Normalise line endings so split always produces one element per visual line
        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        int lastLineIndex = lines.Length - 1;
        int lastLineLength = lines[lastLineIndex].Length;

        Position endOfFile = new(lastLineIndex, lastLineLength);

        return new CodeAction
        {
            Title = "Add matching '/* endregion */'",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] =
                    [
                        new TextEdit
                        {
                            Range = new Range { Start = endOfFile, End = endOfFile },
                            NewText = "\n/* endregion */"
                        }
                    ]
                }
            }
        };
    }

    /// <summary>
    /// Builds a single <see cref="CodeActionKind.SourceOrganizeImports"/> action that removes
    /// every unused <c>#using</c> directive in the document in one operation.
    /// Returns <see langword="null"/> when fewer than two unused directives are present
    /// (the per-diagnostic quick fix already covers the single-occurrence case).
    /// </summary>
    private async Task<CodeAction?> TryCreateRemoveAllUnusedUsingsActionAsync(
        TextDocumentIdentifier document, CancellationToken cancellationToken)
    {
        Script? script = _scriptManager.GetParsedEditor(document);
        if (script is null)
        {
            return null;
        }

        List<Diagnostic> allDiagnostics = await script.GetDiagnosticsAsync(cancellationToken);

        List<Diagnostic> unusedUsings = allDiagnostics
            .Where(d => d.Code is not null
                && d.Code.HasValue && d.Code.Value.IsLong
                && (GSCErrorCodes)(int)d.Code.Value.Long == GSCErrorCodes.UnusedUsing)
            .ToList();

        if (unusedUsings.Count < 2)
        {
            return null;
        }

        // One delete-line edit per unused using, sorted descending by line so that
        // removing a higher line does not shift the positions of lower ones.
        TextEdit[] edits = unusedUsings
            .OrderByDescending(d => d.Range.Start.Line)
            .Select(d =>
            {
                int line = d.Range.Start.Line;
                return new TextEdit
                {
                    Range = new Range { Start = new Position { Line = line, Character = 0 }, End = new Position { Line = line + 1, Character = 0 } },
                    NewText = string.Empty
                };
            }).ToArray();

        return new CodeAction
        {
            Title = $"Remove all unused #using directives ({unusedUsings.Count})",
            Kind = CodeActionKind.SourceOrganizeImports,
            Diagnostics = unusedUsings.ToArray(),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] = edits
                }
            }
        };
    }

    /// <summary>
    /// Removes the <c>thread</c> keyword and its trailing whitespace from a threaded call
    /// whose result is consumed.
    /// <para>
    /// The diagnostic is emitted on a <see cref="PrefixExprNode"/> whose range spans
    /// <c>[thread_token.start, operand.end]</c>, so <c>diagnostic.Range.Start</c> is always
    /// the first character of the <c>thread</c> keyword. Because each consumed-thread diagnostic
    /// carries its own unique range, invoking this fix on a line with multiple such warnings
    /// removes exactly one <c>thread</c> per invocation.
    /// </para>
    /// </summary>
    private static CodeAction? CreateRemoveThreadKeywordAction(
        TextDocumentIdentifier document, Diagnostic diagnostic, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        string? callText = GetTextInRange(content, diagnostic.Range);
        if (callText is null || !callText.StartsWith("thread", StringComparison.Ordinal))
        {
            return null;
        }

        // Consume "thread" (6 chars) then any immediately following whitespace so the
        // replacement text is truly empty rather than leaving a stray space.
        int deleteLen = 6;
        while (deleteLen < callText.Length && callText[deleteLen] is ' ' or '\t')
        {
            deleteLen++;
        }

        Position deleteEnd = new()
        {
            Line = diagnostic.Range.Start.Line,
            Character = diagnostic.Range.Start.Character + deleteLen
        };

        return new CodeAction
        {
            Title = "Remove 'thread' from call",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] =
                    [
                        new TextEdit
                        {
                            Range = new Range { Start = diagnostic.Range.Start, End = deleteEnd },
                            NewText = string.Empty
                        }
                    ]
                }
            }
        };
    }

    /// <summary>
    /// Appends a minimal function stub at the end of the document for a function name
    /// that could not be resolved. The diagnostic range covers the function identifier
    /// token, so <see cref="GetTextInRange"/> gives the name directly.
    /// </summary>
    private static CodeAction? CreateGenerateFunctionStubAction(
        TextDocumentIdentifier document, Diagnostic diagnostic, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        string? functionName = GetTextInRange(content, diagnostic.Range);
        if (string.IsNullOrEmpty(functionName))
        {
            return null;
        }

        // Guard: only proceed when the extracted text is a valid plain identifier.
        // Namespaced calls (ns::func) or anything with odd characters should not produce a stub.
        if (!functionName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            return null;
        }

        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        int lastLineIndex = lines.Length - 1;
        int lastLineLength = lines[lastLineIndex].Length;
        Position endOfFile = new(lastLineIndex, lastLineLength);

        // Append a blank line before the stub so it is visually separated,
        // then close with a trailing newline.
        string stub = $"\n\nfunction {functionName}()\n{{\n}}\n";

        return new CodeAction
        {
            Title = $"Create function '{functionName}'",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] =
                    [
                        new TextEdit
                        {
                            Range = new Range { Start = endOfFile, End = endOfFile },
                            NewText = stub
                        }
                    ]
                }
            }
        };
    }

    /// <summary>
    /// Creates one code action per file that defines symbols in the missing namespace.
    /// Each action inserts a <c>#using</c> directive at the top of the current document
    /// pointing to the file that provides the namespace.
    /// </summary>
    /// <remarks>
    /// The diagnostic range covers the namespace identifier token (e.g. <c>foo</c> in
    /// <c>foo::bar()</c>), so <see cref="GetTextInRange"/> extracts the namespace name directly.
    /// The <c>#using</c> path is derived from the provider file's absolute path by extracting
    /// everything from the <c>scripts/</c> directory onward and stripping the file extension,
    /// producing a path like <c>scripts\zm\utility</c>.
    /// </remarks>
    private List<CodeAction> CreateAddUsingForNamespaceActions(
        TextDocumentIdentifier document, Diagnostic diagnostic, string content)
    {
        var results = new List<CodeAction>();

        if (string.IsNullOrEmpty(content))
        {
            return results;
        }

        // The diagnostic range covers the namespace identifier token
        string? namespaceName = GetTextInRange(content, diagnostic.Range);
        if (string.IsNullOrEmpty(namespaceName))
        {
            return results;
        }

        // Try to extract the function name that follows the namespace token (e.g. "init" from "util::init()").
        // When present, restrict suggestions to files that export the exact ns::function — this prevents
        // offering a #using for a file that happens to share the namespace but doesn't define the function.
        // Fall back to namespace-only search when no qualified member can be extracted.
        string? functionName = TryExtractQualifiedFunctionName(content, diagnostic.Range.End);

        List<string> filePaths = functionName is not null
            ? _scriptManager.SymbolRegistry.FindFilesForNamespacedFunction(namespaceName, functionName)
            : _scriptManager.SymbolRegistry.FindFilesForNamespace(namespaceName);

        if (filePaths.Count == 0)
        {
            return results;
        }

        // Find the insertion point: after the last existing #using line, or at (0,0)
        Position insertPosition = FindUsingInsertPosition(content);
        bool isFirst = filePaths.Count == 1;

        foreach (string filePath in filePaths)
        {
            // Convert the absolute file path to a #using path (e.g. "scripts\zm\utility")
            string? usingPath = ConvertFilePathToUsingPath(filePath);
            if (usingPath is null)
            {
                continue;
            }

            string directive = $"#using {usingPath};\n";

            results.Add(new CodeAction
            {
                Title = $"Add '#using {usingPath}'",
                Kind = CodeActionKind.QuickFix,
                Diagnostics = new Container<Diagnostic>(diagnostic),
                Edit = new WorkspaceEdit
                {
                    Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] =
                        [
                            new TextEdit
                            {
                                Range = new Range { Start = insertPosition, End = insertPosition },
                                NewText = directive
                            }
                        ]
                    }
                }
            });

            // Only the first suggestion is marked as preferred
            isFirst = false;
        }

        return results;
    }

    /// <summary>
    /// Finds the position where a new <c>#using</c> directive should be inserted.
    /// Returns the start of the line immediately after the last existing <c>#using</c>
    /// directive, or <c>(0, 0)</c> if none exist.
    /// </summary>
    private static Position FindUsingInsertPosition(string content)
    {
        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        int lastUsingLine = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("#using", StringComparison.Ordinal))
            {
                lastUsingLine = i;
            }
            // Stop scanning once we pass the header area (first non-blank, non-using,
            // non-comment line that looks like a definition)
            else if (trimmed.Length > 0
                && !trimmed.StartsWith("//", StringComparison.Ordinal)
                && !trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                break;
            }
        }

        // Insert after the last #using, or at the very top
        return lastUsingLine >= 0
            ? new Position { Line = lastUsingLine + 1, Character = 0 }
            : new Position { Line = 0, Character = 0 };
    }

    /// <summary>
    /// Converts an absolute file path to the <c>#using</c> directive path format.
    /// Extracts the portion starting from the <c>scripts/</c> directory and removes
    /// the file extension, producing paths like <c>scripts\zm\utility</c>.
    /// Returns <see langword="null"/> when the file is not inside a <c>scripts/</c> hierarchy.
    /// </summary>
    private static string? ConvertFilePathToUsingPath(string filePath)
    {
        string normalised = filePath.Replace('\\', '/');
        const string marker = "/scripts/";
        int idx = normalised.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (idx < 0)
        {
            return null;
        }

        // Extract from "scripts/" onward (including the "scripts/" prefix)
        string relativePath = normalised[(idx + 1)..]; // skip the leading '/'

        // Remove the file extension (.gsc, .csc, .gsh)
        int dotIndex = relativePath.LastIndexOf('.');
        if (dotIndex > 0)
        {
            relativePath = relativePath[..dotIndex];
        }

        // Convert to backslash-separated form to match GSC convention
        return relativePath.Replace('/', '\\');
    }

    /// <summary>
    /// Offers to create the missing file referenced by a failing <c>#using</c> directive.
    /// <para>
    /// The file is placed in the same scripts root as the current document (the directory
    /// that contains the <c>scripts/</c> folder). The path hierarchy is:
    /// <list type="bullet">
    ///   <item>Mod: <c>&lt;game&gt;\mods\&lt;modname&gt;\scripts\…</c></item>
    ///   <item>Usermap: <c>&lt;game&gt;\usermaps\&lt;mapname&gt;\scripts\…</c></item>
    ///   <item>Root: <c>&lt;TA_GAME_PATH or custom raw&gt;\scripts\…</c></item>
    /// </list>
    /// </para>
    /// </summary>
    private static CodeAction? TryCreateMissingFileAction(
        TextDocumentIdentifier document, Diagnostic diagnostic, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        // The diagnostic range covers only the path segments of the #using directive
        // (e.g. "scripts\m_shared\util_shared") — no keyword, no semicolon.
        string? rawPath = GetTextInRange(content, diagnostic.Range);
        if (string.IsNullOrEmpty(rawPath))
        {
            return null;
        }

        // Derive the file extension from the current document (.gsc / .csc / .gsh)
        string currentFilePath = document.Uri.ToUri().LocalPath;
        string extension = Path.GetExtension(currentFilePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".gsc";
        }

        // Normalise separators and append the extension to get the qualified relative path,
        // matching the format used by GetScriptFilePath (e.g. "scripts/m_shared/util_shared.gsc").
        string qualifiedRelative = rawPath.Replace('\\', '/') + extension;

        // Find the scripts root: the directory that contains the "scripts/" folder,
        // using the same heuristic as ParserUtil.ExtractBasePath.
        string? basePath = ExtractScriptsBasePath(currentFilePath);
        if (basePath is null)
        {
            return null;
        }

        string newFilePath = Path.GetFullPath(
            Path.Combine(basePath, qualifiedRelative.Replace('/', Path.DirectorySeparatorChar)));

        Uri newFileUri = new Uri(newFilePath);
        string fileName = Path.GetFileName(newFilePath);

        return new CodeAction
        {
            Title = $"Create file '{fileName}'",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [DocumentUri.From(newFileUri)] = [new TextEdit { Range = new Range { Start = new Position { Line = 0, Character = 0 }, End = new Position { Line = 0, Character = 0 } }, NewText = "" }]
                }
            }
        };
    }

    /// <summary>
    /// Extracts the root path that sits immediately above the <c>scripts/</c> folder in
    /// <paramref name="filePath"/>. Returns <see langword="null"/> when no such ancestor
    /// can be found (e.g. the file is not inside any <c>scripts</c> hierarchy).
    /// </summary>
    private static string? ExtractScriptsBasePath(string filePath)
    {
        string normalised = filePath.Replace('\\', '/');
        const string marker = "/scripts/";
        int idx = normalised.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (idx < 0)
        {
            return null;
        }

        string basePath = normalised[..idx];

        // On Windows the URI might have a leading slash before the drive letter ("/G:/...")
        if (OperatingSystem.IsWindows()
            && basePath.Length > 2
            && basePath[0] == '/'
            && basePath[2] == ':')
        {
            basePath = basePath[1..];
        }

        return basePath.Replace('/', Path.DirectorySeparatorChar);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Appends <paramref name="lineText"/> on a new line at the very end of the document.
    /// Used for directives that must close an open block, such as <c>#endif</c>.
    /// </summary>
    private static CodeAction? CreateAppendLineAction(
        TextDocumentIdentifier document, Diagnostic diagnostic, string lineText,
        string content, bool isPreferred = false)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        int lastLineIndex = lines.Length - 1;
        int lastLineLength = lines[lastLineIndex].Length;

        Position endOfFile = new() { Line = lastLineIndex, Character = lastLineLength };

        return new CodeAction
        {
            Title = $"Add missing '{lineText}'",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] =
                    [
                        new TextEdit
                        {
                            Range = new Range { Start = endOfFile, End = endOfFile },
                            NewText = $"\n{lineText}"
                        }
                    ]
                }
            }
        };
    }

    /// <summary>
    /// Moves a misplaced <c>#using</c> directive to the very top of the file using two
    /// coordinated edits inside one <see cref="WorkspaceEdit"/>: delete the current line,
    /// then insert it at <c>(0, 0)</c>.
    /// </summary>
    private static CodeAction? CreateMoveUsingToTopAction(
        TextDocumentIdentifier document, Diagnostic diagnostic, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        int diagLine = diagnostic.Range.Start.Line;

        if (diagLine >= lines.Length)
        {
            return null;
        }

        // Capture the raw directive text (strip any trailing \r from CRLF sources)
        string directiveText = lines[diagLine].TrimEnd('\r');

        return new CodeAction
        {
            Title = "Move #using to top of file",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [document.Uri] =
                    [
                        // 1. Delete the misplaced line (including its newline)
                        new TextEdit
                        {
                            Range = new Range { Start = new Position { Line = diagLine, Character = 0 }, End = new Position { Line = diagLine + 1, Character = 0 } },
                            NewText = string.Empty
                        },
                        // 2. Insert it before the first character of the file
                        new TextEdit
                        {
                            Range = new Range { Start = new Position { Line = 0, Character = 0 }, End = new Position { Line = 0, Character = 0 } },
                            NewText = directiveText + "\n"
                        }
                    ]
                }
            }
        };
    }

    /// <summary>
    /// Tries to extract the function-name identifier that follows a <c>::</c> scope-resolution
    /// operator immediately after the namespace token whose range ends at
    /// <paramref name="namespaceTokenEnd"/>.
    /// For example, given source <c>util::init()</c> and a position pointing just past
    /// <c>util</c>, this returns <c>"init"</c>.
    /// Returns <c>null</c> when no <c>::identifier</c> is found (e.g. the namespace token
    /// is not followed by a qualified member on the same line).
    /// </summary>
    private static string? TryExtractQualifiedFunctionName(string content, Position namespaceTokenEnd)
    {
        string[] lines = content.ReplaceLineEndings("\n").Split('\n');

        int lineIdx = namespaceTokenEnd.Line;
        if (lineIdx >= lines.Length)
            return null;

        string line = lines[lineIdx];
        int col = namespaceTokenEnd.Character;

        // Expect "::" immediately after the namespace token
        if (col + 1 >= line.Length || line[col] != ':' || line[col + 1] != ':')
            return null;

        int nameStart = col + 2;
        if (nameStart >= line.Length || (!char.IsAsciiLetter(line[nameStart]) && line[nameStart] != '_'))
            return null;

        int nameEnd = nameStart;
        while (nameEnd < line.Length && (char.IsAsciiLetterOrDigit(line[nameEnd]) || line[nameEnd] == '_'))
            nameEnd++;

        return line[nameStart..nameEnd];
    }

    /// <summary>
    /// Extracts the source text that falls within the given LSP <paramref name="range"/>.
    /// Returns <see langword="null"/> when the range references lines outside the document.
    /// </summary>
    private static string? GetTextInRange(string content, Range range)
    {
        // Normalise so we always split on \n only
        string[] lines = content.ReplaceLineEndings("\n").Split('\n');

        int startLine = range.Start.Line;
        int endLine = range.End.Line;

        if (startLine >= lines.Length || endLine >= lines.Length)
        {
            return null;
        }

        // Single-line range — common case
        if (startLine == endLine)
        {
            string line = lines[startLine];
            int startChar = Math.Min(range.Start.Character, line.Length);
            int endChar = Math.Min(range.End.Character, line.Length);
            return line[startChar..endChar];
        }

        // Multi-line range
        var sb = new StringBuilder();
        sb.Append(lines[startLine][Math.Min(range.Start.Character, lines[startLine].Length)..]);

        for (int i = startLine + 1; i < endLine; i++)
        {
            sb.Append('\n');
            sb.Append(lines[i]);
        }

        string lastLine = lines[endLine];
        sb.Append('\n');
        sb.Append(lastLine[..Math.Min(range.End.Character, lastLine.Length)]);

        return sb.ToString();
    }
}




