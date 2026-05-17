using GSCode.Parser;
using GSCode.Parser.SA;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
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
        if (script is null) return null;

        string newName = request.NewName;
        var edits = new Dictionary<DocumentUri, List<TextEdit>>();

        void AddEdit(Uri uri, Range range)
        {
            DocumentUri key = uri;
            if (!edits.TryGetValue(key, out var list))
            {
                list = new List<TextEdit>();
                edits[key] = list;
            }
            list.Add(new TextEdit { Range = range, NewText = newName });
        }

        // --- Path 1: dot-field access (e.g. level.foo) — cross-file, same languageId ---
        var fieldAccess = await script.GetGlobalFieldAtAsync(request.Position, cancellationToken);
        if (fieldAccess is not null)
        {
            var fieldKey = new SymbolKey(GSCode.Parser.SA.SymbolKind.Field, "", fieldAccess.Value.Field);
            foreach (var loaded in scriptManager.GetLoadedScripts())
            {
                if (!string.Equals(loaded.Script.LanguageId, script.LanguageId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (loaded.Script.References.TryGetValue(fieldKey, out var ranges))
                    foreach (var r in ranges)
                        AddEdit(loaded.Uri, r);
            }
            return BuildWorkspaceEdit(edits);
        }

        // --- Path 2: local variable within function scope — single file only ---
        var localRefs = await script.GetLocalVariableReferencesAsync(
            request.Position, includeDeclaration: true, cancellationToken);
        if (localRefs.Count > 0)
        {
            var fileUri = request.TextDocument.Uri.ToUri();
            foreach (var r in localRefs)
                AddEdit(fileUri, r);
            return BuildWorkspaceEdit(edits);
        }

        // --- Path 3: function / class — cross-file, same languageId ---
        var qid = await script.GetQualifiedIdentifierAtAsync(request.Position, cancellationToken);
        if (qid is null) return null;

        string ns = qid.Value.qualifier ?? (script.DefinitionsTable?.CurrentNamespace ?? "");
        string name = qid.Value.name;

        var keys = new[]
        {
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Function, ns, name),
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Method,   ns, name),
            new SymbolKey(GSCode.Parser.SA.SymbolKind.Class,    ns, name)
        };

        string requestingLanguageId = script.LanguageId;

        foreach (var loaded in scriptManager.GetLoadedScripts())
        {
            if (!string.Equals(loaded.Script.LanguageId, requestingLanguageId, StringComparison.OrdinalIgnoreCase))
                continue;

            // Reference call-sites
            foreach (var key in keys)
            {
                if (loaded.Script.References.TryGetValue(key, out var ranges))
                    foreach (var r in ranges)
                        AddEdit(loaded.Uri, r);
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
                    AddEdit(new Uri(resolved), loc.Value.Range.ToRange());
                }
            }
        }

        if (edits.Count == 0) return null;
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
