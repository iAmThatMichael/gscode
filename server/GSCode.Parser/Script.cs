using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.Pre;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using GSCode.Parser.SA;
using GSCode.Parser.Misc;
using System.IO;
using GSCode.Parser.SPA;
using System.Text.RegularExpressions;
using GSCode.Parser.DFA;
using System.Runtime.CompilerServices;
using Serilog;
using GSCode.Parser.Util;

namespace GSCode.Parser;

using SymbolKindSA = GSCode.Parser.SA.SymbolKind;

public partial class Script(Uri ScriptUri, string languageId, ISymbolLocationProvider? globalSymbolProvider = null, ScriptMode mode = ScriptMode.Editor, IGlobalFieldProvider? globalFieldProvider = null)
{
    public bool Failed { get; private set; } = false;
    public bool Parsed { get; private set; } = false;
    public bool Analysed { get; private set; } = false;

    internal ParserIntelliSense Sense { get; private set; } = default!;

    public string LanguageId { get; } = languageId;

    private Task? ParsingTask { get; set; } = null;
    private Task? AnalysisTask { get; set; } = null;

    private readonly TaskCompletionSource _parseInitiated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _analysisInitiated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ScriptNode? RootNode { get; set; } = null;

    /// <summary>
    /// Optional global symbol location provider for workspace-wide O(1) lookups.
    /// </summary>
    private ISymbolLocationProvider? GlobalSymbolProvider { get; } = globalSymbolProvider;

    public DefinitionsTable? DefinitionsTable { get; private set; } = default;

    public IEnumerable<Uri> Dependencies => DefinitionsTable?.Dependencies ?? [];

    // Expose macro outlines for outliner without exposing Sense outside assembly
    public IReadOnlyList<MacroOutlineItem> MacroOutlines => Sense?.MacroOutlines ?? [];

    // Precomputed function scope data (populated after analysis, before AST disposal)
    private sealed record FunctionScopeInfo(string? FunctionName, Range BodyRange, List<(string Name, Range Range)> Parameters);
    private List<FunctionScopeInfo>? _functionScopes;

    // Reference index: map from symbol key to all ranges in this file
    private readonly Dictionary<SymbolKey, List<Range>> _references = new();
    public IReadOnlyDictionary<SymbolKey, List<Range>> References => _references;

    // Cached/interned strings for deduplication
    private string? _scriptFileName;
    private string ScriptFileName => _scriptFileName ??= Path.GetFileNameWithoutExtension(ScriptUri.LocalPath);

    // Common markdown format strings (interned for memory efficiency)
    private static readonly string s_gscCodeBlockStart = string.Intern("```gsc\n");
    private static readonly string s_codeBlockEnd = string.Intern("\n```");
    private static readonly string s_markdownSeparator = string.Intern("\n---\n");

    /// <summary>
    /// Gets the effective namespace - either from DefinitionsTable or falls back to script filename.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetEffectiveNamespace() => DefinitionsTable?.CurrentNamespace ?? ScriptFileName;

    // Use shared API instance to avoid redundant allocations across scripts
    private ScriptAnalyserData? TryGetApi() => ScriptAnalyserData.GetShared(LanguageId);

    private bool IsBuiltinFunction(string name)
    {
        var api = TryGetApi();
        if (api is null) return false;
        try { return api.GetApiFunction(name) is not null; }
        catch { return false; }
    }

    /// <summary>
    /// Records a pipeline step failure: sets <see cref="Failed"/>, logs the exception,
    /// and emits an IDE diagnostic. Call from a catch block to collapse repeated boilerplate.
    /// </summary>
    private void RecordStepFailure(Exception ex, string stepName, GSCErrorCodes errorCode)
    {
        Failed = true;
        Log.Error(ex, "Failed to {StepName} script.", stepName);
        Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), errorCode, ex.GetType().Name);
    }

    public async Task ParseAsync(string documentText)
    {
        ParsingTask = DoParseAsync(documentText);
        _parseInitiated.TrySetResult();
        await ParsingTask;
    }

    public Task DoParseAsync(string documentText)
    {
        // Guard: reject files that exceed ushort range limits for TokenRange
        {
            int lineCount = 1;
            int lineLength = 0;
            foreach (char c in documentText)
            {
                if (c == '\n')
                {
                    lineCount++;
                    lineLength = 0;
                }
                else
                {
                    lineLength++;
                }

                if (lineCount > TokenRange.MaxLine || lineLength > TokenRange.MaxChar)
                {
                    Failed = true;
                    Sense = new(0, ScriptUri, LanguageId, mode);
                    Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledLexError,
                        $"File too large to parse ({lineCount} lines, max line length {lineLength})");
                    return Task.CompletedTask;
                }
            }
        }

        LinkedToken startNode;
        LinkedToken endNode;
        try
        {
            // Transform the document text into a token sequence
            Lexer lexer = new(documentText.AsSpan());
            (startNode, endNode) = lexer.Transform();
        }
        catch (Exception ex)
        {
            // Failed to parse the script
            Failed = true;
            Log.Error(ex, "Failed to tokenise script.");

            // Create a dummy IntelliSense container so we can provide an error to the IDE.
            Sense = new(0, ScriptUri, LanguageId, mode);
            Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledLexError, ex.GetType().Name);

            return Task.CompletedTask;
        }

        ParserIntelliSense sense = Sense = new(endLine: endNode.TokenRange.EndLine, ScriptUri, LanguageId, mode);

        // Preprocess the tokens.
        Preprocessor preprocessor = new(startNode, sense);
        try { preprocessor.Process(); }
        catch (Exception ex) { RecordStepFailure(ex, "preprocess", GSCErrorCodes.UnhandledMacError); return Task.CompletedTask; }

        // Build a library of tokens so IntelliSense can quickly lookup a token at a given position.
        Sense.CommitTokens(startNode);

        // Build the AST.
        AST.Parser parser = new(startNode, sense, LanguageId);

        try { RootNode = parser.Parse(); }
        catch (Exception ex) { RecordStepFailure(ex, "AST-gen", GSCErrorCodes.UnhandledAstError); return Task.CompletedTask; }

        // Gather signatures for all functions and classes.
        DefinitionsTable = new(ScriptFileName, GlobalSymbolProvider);

        // Set the DefinitionsTable in the completions library (editor mode only)
        if (sense.IsEditorMode)
        {
            sense.SetDefinitionsTable(DefinitionsTable);
            sense.SetGlobalFieldProvider(globalFieldProvider);
        }

        SignatureAnalyser signatureAnalyser = new(RootNode!, DefinitionsTable, Sense);
        try { signatureAnalyser.Analyse(); }
        catch (Exception ex) { RecordStepFailure(ex, "signature analyse", GSCErrorCodes.UnhandledSaError); return Task.CompletedTask; }

        // Editor-only: folding ranges and reference index
        if (Sense.IsEditorMode)
        {
            // Analyze folding ranges from the token stream
            UserRegionsAnalyser foldingRangeAnalyser = new(startNode, Sense);
            try { foldingRangeAnalyser.Analyse(); }
            catch (Exception ex) { RecordStepFailure(ex, "analyse folding ranges", GSCErrorCodes.UnhandledSaError); return Task.CompletedTask; }

            // Build references index from token stream
            BuildReferenceIndex();
        }


        Parsed = true;
        return Task.CompletedTask;
    }

    public async Task AnalyseAsync(IEnumerable<IExportedSymbol> exportedSymbols, CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        AnalysisTask = DoAnalyseAsync(exportedSymbols, cancellationToken);
        _analysisInitiated.TrySetResult();
        await AnalysisTask;
    }

    public Task DoAnalyseAsync(IEnumerable<IExportedSymbol> exportedSymbols, CancellationToken cancellationToken = default)
    {
        if (Failed || DefinitionsTable is null)
        {
            return Task.CompletedTask;
        }

        string fileName = System.IO.Path.GetFileName(ScriptUri.LocalPath);

        // Get a comprehensive list of symbols available in this context.
        Dictionary<string, IExportedSymbol> allSymbols = new(DefinitionsTable.InternalSymbols, StringComparer.OrdinalIgnoreCase);
        foreach (IExportedSymbol symbol in exportedSymbols)
        {
            // Add dependency symbols, but don't overwrite local symbols (local takes precedence).
            if (symbol.Type == ExportedSymbolType.Function)
            {
                ScrFunction function = (ScrFunction)symbol;
                string qualifiedName = $"{function.Namespace}::{function.Name}";
                allSymbols.TryAdd(qualifiedName, symbol);
                // Also add to DefinitionsTable.InternalSymbols for completion
                DefinitionsTable.InternalSymbols.TryAdd(qualifiedName, symbol);
                if (!function.Implicit)
                {
                    continue;
                }
            }
            string symbolName = symbol.Name;
            allSymbols.TryAdd(symbolName, symbol);
            // Also add to DefinitionsTable.InternalSymbols for completion
            DefinitionsTable.InternalSymbols.TryAdd(symbolName, symbol);
        }

        // Build set of known namespaces from function and class definitions
        var knownNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DefinitionsTable.CurrentNamespace };
        knownNamespaces.UnionWith(DefinitionsTable.GetAllFunctionLocations().Select(kv => kv.Key.Qualifier));
        knownNamespaces.UnionWith(DefinitionsTable.GetAllClassLocations().Select(kv => kv.Key.Qualifier));

        ControlFlowAnalyser controlFlowAnalyser = new(Sense, DefinitionsTable!);
        try { controlFlowAnalyser.Run(); }
        catch (Exception ex) { RecordStepFailure(ex, "run control flow analyser", GSCErrorCodes.UnhandledSpaError); return Task.CompletedTask; }

        DataFlowAnalyser dataFlowAnalyser = new(controlFlowAnalyser.FunctionGraphs, controlFlowAnalyser.ClassGraphs, Sense, allSymbols, TryGetApi(), DefinitionsTable.CurrentNamespace, knownNamespaces, fileName, DefinitionsTable);
        try { dataFlowAnalyser.Run(); }
        catch (Exception ex) { RecordStepFailure(ex, "run data flow analyser", GSCErrorCodes.UnhandledSpaError); return Task.CompletedTask; }

        // Basic SPA diagnostics (editor-only: rely on _references and Sense.Tokens)
        if (Sense.IsEditorMode && RootNode is not null)
        {
            try
            {
                ScriptDiagnosticsAnalyser diagnosticsAnalyser = new(RootNode, Sense, DefinitionsTable, _references, LanguageId);
                diagnosticsAnalyser.Run();
            }
            catch (Exception ex)
            {
                // Non-fatal: don't fail analysis entirely; surface as SPA failure
                Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledSpaError, ex.GetType().Name);
            }
        }

        // === Memory compaction ===
        if (Sense.IsEditorMode)
        {
            Sense.FinalizeSemanticTokens();
            PrecomputeFunctionScopes();
        }
        else
        {
            // Index mode: token list was needed for SignatureAnalyser but can be freed now
            Sense.Tokens.Clear();
            // Analysis-time data duplicates the global symbol registry — free it
            DefinitionsTable!.StripAnalysisData();
        }
        DefinitionsTable!.StripAstReferences();
        RootNode = null;

        Analysed = true;
        return Task.CompletedTask;
    }

    public async Task<List<Diagnostic>> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        // TODO: maybe a mechanism to check if analysed if that's a requirement

        // We still expose diagnostics even if the script failed to parse
        await WaitUntilParsedAsync(cancellationToken);
        return Sense.Diagnostics;
    }

    public async Task<IReadOnlyList<ISemanticToken>> GetSemanticTokensAsync(CancellationToken cancellationToken)
    {
        if (!Sense.IsEditorMode) return [];
        await WaitUntilAnalysedAsync(cancellationToken);
        // SemanticTokens are already sorted by FinalizeSemanticTokens (called after analysis).
        return Sense.SemanticTokens;
    }

    public async Task<CompletionList?> GetCompletionAsync(Position position, CancellationToken cancellationToken)
    {
        if (!Sense.IsEditorMode) return null;
        await WaitUntilAnalysedAsync(cancellationToken);
        return Sense.Completions!.GetCompletionsFromPosition(position);
    }

    public async Task<IEnumerable<FoldingRange>> GetFoldingRangesAsync(CancellationToken cancellationToken = default)
    {
        if (!Sense.IsEditorMode) return [];
        await WaitUntilParsedAsync(cancellationToken);
        return Sense.FoldingRanges;
    }

    private async Task WaitUntilParsedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ParsingTask is null)
        {
            await _parseInitiated.Task.WaitAsync(cancellationToken);
        }
        await ParsingTask!;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task WaitUntilAnalysedAsync(CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (AnalysisTask is null)
        {
            await _analysisInitiated.Task.WaitAsync(cancellationToken);
        }
        await AnalysisTask!;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<IEnumerable<IExportedSymbol>> IssueExportedSymbolsAsync(CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        if (DefinitionsTable is null)
            return [];

        var functions = DefinitionsTable.ExportedFunctions ?? [];
        var classes = DefinitionsTable.ExportedClasses ?? [];
        return functions.Cast<IExportedSymbol>().Concat(classes);
    }

    /// <summary>
    /// Set of identifier lexemes (lowered) that are tracked as global field owners.
    /// Extend this set to track additional globals (e.g., <c>self</c> in the future).
    /// </summary>
    private static readonly HashSet<string> s_trackedOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "level",
        "world",
        "game"
    };

    /// <summary>
    /// Extracts global-object field accesses from the token stream.
    /// Scans for patterns like <c>level.fieldName</c>, <c>world.foo</c>, <c>game["key"]</c> is NOT tracked (array access).
    /// Only dot-access patterns (<c>Identifier → Dot → Identifier</c>) are extracted.
    /// </summary>
    /// <returns>
    /// A list of (ownerName, fieldName) pairs found in this script.
    /// <c>ownerName</c> is the lowered identifier (e.g., "level"), <c>fieldName</c> is the original casing.
    /// </returns>
    public List<(string OwnerName, string FieldName)> ExtractGlobalFieldAccesses()
    {
        var results = new List<(string, string)>();
        if (!Parsed) return results;

        foreach (Token token in Sense.Tokens.GetAll())
        {
            // We're looking for the dot in:  Identifier("level") → Dot → Identifier("fieldName")
            if (token.Type != TokenType.Dot)
                continue;

            // Look back to see if the token before the dot is a tracked global identifier
            Token? ownerToken = token.PreviousNonWhitespace();
            if (ownerToken is null || ownerToken.Type != TokenType.Identifier)
                continue;

            if (!s_trackedOwners.Contains(ownerToken.Lexeme))
                continue;

            // Look forward to get the field name
            Token? fieldToken = token.NextNonWhitespace();
            if (fieldToken is null || fieldToken.Type != TokenType.Identifier)
                continue;

            results.Add((ownerToken.Lexeme.ToLowerInvariant(), fieldToken.Lexeme));
        }

        return results;
    }
}
