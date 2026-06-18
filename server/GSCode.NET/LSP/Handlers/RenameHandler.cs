using GSCode.Data;
using GSCode.Parser;
using GSCode.Parser.SA;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;
using System.IO;
using System.Linq;

namespace GSCode.NET.LSP.Handlers;

internal class RenameHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : RenameHandlerBase
{
    public override async Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken cancellationToken)
    {
        Script? script = scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            Log.Warning("[Rename] No parsed script for {Uri}", request.TextDocument.Uri);
            return null;
        }

        string newName = request.NewName;
        var edits = new Dictionary<DocumentUri, List<TextEdit>>();
        var seen = new HashSet<(string Uri, int Sl, int Sc, int El, int Ec)>();

        void AddEdit(Uri uri, Range range)
        {
            DocumentUri key = uri;
            var dedupeKey = (key.ToString(), range.Start.Line, range.Start.Character, range.End.Line, range.End.Character);
            if (!seen.Add(dedupeKey)) return;

            if (!edits.TryGetValue(key, out var list))
            {
                list = new List<TextEdit>();
                edits[key] = list;
            }
            list.Add(new TextEdit { Range = range, NewText = newName });
        }

        // --- Path 1: dot-field access (e.g. level.foo) — cross-file, same language ---
        var fieldAccess = await script.GetGlobalFieldAtAsync(request.Position, cancellationToken);
        if (fieldAccess is not null)
        {
            Log.Debug("[Rename] Path 1 (field): '{Field}' → '{NewName}'", fieldAccess.Value.Field, newName);
            var fieldKey = new SymbolKey(GSCode.Parser.SA.SymbolKind.Field, "", fieldAccess.Value.Field);
            foreach (var loaded in scriptManager.GetLoadedScripts(script.Language))
            {
                if (loaded.Script.References.TryGetValue(fieldKey, out var ranges))
                    foreach (var r in ranges)
                        AddEdit(loaded.Uri, r);
            }
            Log.Debug("[Rename] Path 1: {Count} edit(s) across {Files} file(s)", edits.Values.Sum(l => l.Count), edits.Count);
            return BuildWorkspaceEdit(edits);
        }

        // --- Path 2: local variable within function scope — single file only ---
        var localRefs = await script.GetLocalVariableReferencesAsync(
            request.Position, includeDeclaration: true, cancellationToken);
        if (localRefs.Count > 0)
        {
            Log.Debug("[Rename] Path 2 (local var): {Count} reference(s) → '{NewName}'", localRefs.Count, newName);
            var fileUri = request.TextDocument.Uri.ToUri();
            foreach (var r in localRefs)
                AddEdit(fileUri, r);
            return BuildWorkspaceEdit(edits);
        }

        // --- Path 3: function / class — cross-file, same language ---
        var qid = await script.GetQualifiedIdentifierAtAsync(request.Position, cancellationToken);
        if (qid is null)
        {
            Log.Warning("[Rename] No renameable identifier at {Position} in {Uri}", request.Position, request.TextDocument.Uri);
            return null;
        }

        string ns = (qid.Value.qualifier ?? (script.DefinitionsTable?.CurrentNamespace ?? "")).ToLowerInvariant();
        string name = qid.Value.name.ToLowerInvariant();
        Log.Debug("[Rename] Path 3 (fn/class): ns='{Ns}' name='{Name}' → '{NewName}'", ns, name, newName);

        var keys = new[]
        {
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Function, ns, name),
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Method,   ns, name),
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Class,    ns, name)
        };

        ScriptLanguage requestingLanguage = script.Language;

        foreach (var loaded in scriptManager.GetLoadedScripts(requestingLanguage))
        {
            // Reference call-sites
            foreach (var key in keys)
            {
                if (loaded.Script.References.TryGetValue(key, out var ranges))
                {
                    Log.Debug("[Rename] Found {Count} reference(s) for {Key} in {Uri}", ranges.Count, key, loaded.Uri);
                    foreach (var r in ranges)
                        AddEdit(loaded.Uri, r);
                }
            }

            // Declaration site
            if (loaded.Script.DefinitionsTable is not null)
            {
                foreach (var key in keys)
                {
                    var loc = loaded.Script.DefinitionsTable.GetSymbolLocation(key.Namespace, key.Name);
                    if (loc is null) continue;

                    string? resolved = ScriptFileResolver.GetScriptFilePath(loaded.Uri.LocalPath, loc.Value.FilePath);
                    if (resolved is null || !File.Exists(resolved)) continue;

                    // Defense-in-depth: DefinitionsTable.GetSymbolLocation falls back to the global
                    // registry, which could theoretically resolve across language boundaries.
                    // Verify the resolved file belongs to the same language as the requesting script.
                    if (ScriptLanguageExtensions.FromExtension(Path.GetExtension(resolved)) != requestingLanguage)
                        continue;

                    string normalizedResolved = ScriptFileResolver.NormalizeFilePathForUri(resolved);
                    Uri declUri = new Uri(normalizedResolved);

                    // GetSymbolLocation falls back to the global registry, so every loaded script
                    // resolves the same declaration. Only emit the edit from the script that owns it;
                    // otherwise we get N duplicate declaration edits (one per loaded script).
                    if (!UriComparer.OrdinalIgnoreCase.Equals(declUri, loaded.Uri))
                        continue;

                    Log.Debug("[Rename] Declaration site: normalized='{Normalized}' loadedUri='{LoadedUri}'",
                        normalizedResolved, loaded.Uri);
                    AddEdit(declUri, loc.Value.Range.ToRange());
                }
            }
        }

        if (edits.Count == 0)
        {
            Log.Warning("[Rename] No edits found for ns='{Ns}' name='{Name}'", ns, name);
            return null;
        }

        Log.Debug("[Rename] Returning {EditCount} edit(s) across {FileCount} file(s)", edits.Values.Sum(l => l.Count), edits.Count);
        return BuildWorkspaceEdit(edits);
    }

    private static WorkspaceEdit BuildWorkspaceEdit(Dictionary<DocumentUri, List<TextEdit>> edits)
        => new()
        {
            Changes = edits.ToDictionary(
                kvp => kvp.Key,
                kvp => (IEnumerable<TextEdit>)kvp.Value)
        };

    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = documentSelector,
            PrepareProvider = true
        };
}
