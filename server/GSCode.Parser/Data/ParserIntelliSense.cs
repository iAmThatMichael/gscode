using GSCode.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace GSCode.Parser.Data;

public enum ScriptMode { Editor, Index }

public sealed record class SemanticToken(Range Range, string SemanticTokenType, string[] SemanticTokenModifiers) : ISemanticToken;

public enum DeferredSymbolType
{
    Function,
    Class
}
public sealed record class DeferredSymbol(Range Range, string? Namespace, string Value);

// Macro outline item now includes a source display (e.g., shared/shared.gsh) if known
public sealed record class MacroOutlineItem(string Name, Range Range, string? SourceDisplay = null);

// Track #insert regions to map generated tokens back to their origin file
public sealed record class InsertRegion(Range Range, string RawPath, string? ResolvedPath);

internal sealed class ParserIntelliSense
{
    private static readonly Comparison<ISemanticToken> s_semanticTokenComparison = (x, y) =>
    {
        int lineComparison = x.Range.Start.Line.CompareTo(y.Range.Start.Line);
        if (lineComparison != 0) return lineComparison;
        return x.Range.Start.Character.CompareTo(y.Range.Start.Character);
    };

    public ScriptMode Mode { get; }
    public bool IsEditorMode => Mode == ScriptMode.Editor;

    // ── Always populated (both Editor and Index modes) ──
    // These are needed for preprocessing, macro tracking, and symbol extraction.
    /// <summary>
    /// Insert regions (range on the source line) to resolved file path mapping.
    /// </summary>
    public List<InsertRegion> InsertRegions { get; } = new();

    /// <summary>
    /// Macro definitions for completions and IntelliSense, with source file info.
    /// Key: macro name, Value: (definition, source display like "shared/shared.gsh")
    /// </summary>
    public Dictionary<string, (Pre.MacroDefinition Definition, string? SourceDisplay)> MacroDefinitions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Library of tokens to quickly lookup a token at a given position.
    /// </summary>
    public DocumentTokensLibrary Tokens { get; } = new();

    /// <summary>
    /// List of dependencies to request from the Language Server.
    /// </summary>
    public List<DocumentUri> Dependencies { get; } = new();

    /// <summary>
    /// List of diagnostics to push to the editor.
    /// </summary>
    public List<Diagnostic> Diagnostics { get; } = new();

    /// <summary>
    /// When true, AddSenseToken calls are suppressed. Used during dataflow analysis
    /// worklist phase to prevent incomplete type information from being recorded.
    /// </summary>
    public bool SilentSenseTokens { get; set; } = false;

    // ── Editor-only (null/empty in Index mode) ──
    // These support IDE presentation features and are not needed during indexing.
    /// <summary>
    /// Hover storage for IntelliSense. Null in index mode.
    /// </summary>
    public DocumentHoversLibrary? HoverLibrary { get; }

    /// <summary>
    /// List of folding ranges to push to the editor.
    /// </summary>
    public List<FoldingRange> FoldingRanges { get; } = [];

    /// <summary>
    /// List of semantic tokens to push to the editor.
    /// </summary>
    public List<ISemanticToken> SemanticTokens { get; } = new();
    private bool _semanticTokensSorted = false;

    private readonly string _scriptPath;
    public readonly string _languageId;
    public string ScriptPath => _scriptPath;
    public string ScriptUri { get; }

    /// <summary>
    /// Macros discovered during preprocessing for use in the outliner.
    /// </summary>
    public List<MacroOutlineItem> MacroOutlines { get; } = new();

    /// <summary>
    /// Library of completions to quickly lookup completions at a given position. Null in index mode.
    /// </summary>
    public DocumentCompletionsLibrary? Completions { get; }

    public ParserIntelliSense(int endLine, DocumentUri scriptUri, string languageId, ScriptMode mode = ScriptMode.Editor)
    {
        Mode = mode;
        _scriptPath = scriptUri.Path;
        ScriptUri = scriptUri.Path;
        _languageId = languageId;

        if (mode == ScriptMode.Editor)
        {
            HoverLibrary = new(endLine + 1);
            Completions = new(Tokens, languageId, scriptUri.Path);
        }
    }

    public void AddInsertRegion(Range range, string rawPath, string? resolvedPath)
    {
        // Note: InsertRegions must be populated in ALL modes (including Index),
        // because Preprocessor.Define() uses them to attribute macros from #insert'd
        // GSH files to their source path for MacroDefinitionCache tracking.
        InsertRegions.Add(new InsertRegion(range, rawPath, resolvedPath));
    }

    public void AddMacroOutline(string name, Range range, string? sourceDisplay = null)
    {
        if (!IsEditorMode) return;
        MacroOutlines.Add(new MacroOutlineItem(name, range, sourceDisplay));
    }

    public void AddMacroDefinition(string name, Pre.MacroDefinition definition, string? sourceDisplay = null)
    {
        MacroDefinitions[name] = (definition, sourceDisplay);
    }

    public void SetDefinitionsTable(SA.DefinitionsTable? definitionsTable)
    {
        if (Completions is null) return;
        Completions.DefinitionsTable = definitionsTable;
        Completions.MacroDefinitions = MacroDefinitions;
    }

    /// <summary>
    /// Sparse store for token → ISenseDefinition mappings.
    /// (identifiers with hovers/highlighting) are stored here, avoiding an 8-byte pointer on every token.
    /// </summary>
    private readonly Dictionary<Token, ISenseDefinition> _senseDefinitions = new(ReferenceEqualityComparer.Instance);

    public ISenseDefinition? GetSenseDefinition(Token token)
        => _senseDefinitions.GetValueOrDefault(token);

    public void AddSenseToken(Token token, ISenseDefinition definition)
    {
        if (!IsEditorMode) return;

        // Suppress during dataflow worklist phase to avoid recording incomplete type info.
        if (SilentSenseTokens)
        {
            return;
        }

        // The token is from an insert/macro (which we don't show) or it's already had a definition pushed.
        // In these cases, skip (for existing, the first gets precedence).
        if (token.IsFromPreprocessor || _senseDefinitions.ContainsKey(token))
        {
            return;
        }

        _senseDefinitions[token] = definition;

        AddSenseDefinition(definition);
    }

    public void AddSenseDefinition(ISenseDefinition definition)
    {
        if (!IsEditorMode) return;
        SemanticTokens.Add(definition);
        _semanticTokensSorted = false;
        HoverLibrary!.Add(definition);
    }

    /// <summary>
    /// Sorts and deduplicates semantic tokens. Call once after all tokens have been added.
    /// </summary>
    public void FinalizeSemanticTokens()
    {
        if (!IsEditorMode) return;
        if (_semanticTokensSorted) return;
        SemanticTokens.Sort(s_semanticTokenComparison);
        _semanticTokensSorted = true;
    }

    public void AddDiagnostic(Range range, string source, GSCErrorCodes code, params object?[] args)
    {
        Diagnostics.Add(DiagnosticCodes.GetDiagnostic(range, source, code, args));
    }

    public void AddSpaDiagnostic(Range range, GSCErrorCodes code, params object?[] args) => AddDiagnostic(range, DiagnosticSources.Spa, code, args);
    public void AddAstDiagnostic(Range range, GSCErrorCodes code, params object?[] args) => AddDiagnostic(range, DiagnosticSources.Ast, code, args);
    public void AddPreDiagnostic(Range range, GSCErrorCodes code, params object?[] args) => AddDiagnostic(range, DiagnosticSources.Preprocessor, code, args);
    public void AddIdeDiagnostic(Range range, GSCErrorCodes code, params object?[] args) => AddDiagnostic(range, DiagnosticSources.Ide, code, args);

    public void AddDependency(string scriptPath)
    {
        Dependencies.Add(new Uri(scriptPath));
    }

    public string? GetDependencyPath(string dependencyPath, Range sourceRange)
    {
        string qualifiedDependencyPath = dependencyPath + "." + _languageId;
        string? resolvedPath = ParserUtil.GetScriptFilePath(_scriptPath, qualifiedDependencyPath);
        if (resolvedPath is null)
        {
            AddSpaDiagnostic(sourceRange, GSCErrorCodes.MissingUsingFile, qualifiedDependencyPath);
            return null;
        }

        return resolvedPath;
    }

    /// <summary>
    /// Cache of lexed token lists for #insert files, keyed by resolved absolute path.
    /// Shared across all ParserIntelliSense instances to avoid re-reading and re-lexing
    /// the same included file for every script that inserts it.
    /// </summary>
    private static readonly ConcurrentDictionary<string, TokenList> _insertTokenCache = new();

    public TokenList? GetFileTokens(string dependencyPath, TokenRange? belongToRange = null)
    {
        string? resolvedPath = ParserUtil.GetScriptFilePath(_scriptPath, dependencyPath);

        // Sanity check the result
        if (resolvedPath is null || !File.Exists(resolvedPath))
        {
            return null;
        }

        // Cache the lexed tokens by resolved path — clone for each consumer
        // so token linking in the preprocessor doesn't corrupt the cached copy.
        var cachedTokens = _insertTokenCache.GetOrAdd(resolvedPath, path =>
        {
            string contents = File.ReadAllText(path);
            Lexer lexer = new(contents.AsSpan());
            return lexer.Transform();
        });

        return cachedTokens.CloneList(belongToRange);
    }

    public void CommitTokens(LinkedToken startNode)
    {
        Tokens.AddRange(startNode);
    }

    public string? ResolveInsertPath(string dependencyPath, Range sourceRange)
    {
        string? resolvedPath = ParserUtil.GetScriptFilePath(_scriptPath, dependencyPath);
        if (resolvedPath is null)
        {
            AddPreDiagnostic(sourceRange, GSCErrorCodes.MissingInsertFile, dependencyPath);
            return null;
        }
        return resolvedPath;
    }

    /* Others to support:
     * Inlay hint
     * Go to declaration
     * Go to definition
     * Go to implementation
     * Find references
     * Code lens
     * Signature help
     * Completion items
     * Rename
     * ...
     */
}
