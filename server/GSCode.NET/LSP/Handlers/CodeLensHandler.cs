using GSCode.Parser;
using GSCode.Parser.SA;
using GSCode.Parser.Util;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Linq;

namespace GSCode.NET.LSP.Handlers;

/// <summary>
/// Provides "N references" code lenses above every function and class declaration
/// in the open file. The initial pass lists declaration positions; the resolve pass
/// counts cross-file references lazily.
/// </summary>
internal class CodeLensHandler(
    ScriptManager scriptManager,
    TextDocumentSelector documentSelector) : CodeLensHandlerBase
{
    // JSON field names used to ferry lookup data through the resolve round-trip.
    private const string DataNs   = "ns";
    private const string DataName = "name";
    private const string DataKind = "kind";
    private const string DataUri  = "uri";

    // kind tag values
    private const string KindFunction = "function";
    private const string KindClass    = "class";

    // -------------------------------------------------------------------------
    // Pass 1 — list a lens for every declaration in the current file
    // -------------------------------------------------------------------------
    public override async Task<CodeLensContainer?> Handle(
        CodeLensParams request, CancellationToken cancellationToken)
    {
        Script? script = scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null || script.DefinitionsTable is null)
            return new CodeLensContainer();

        cancellationToken.ThrowIfCancellationRequested();

        string currentPath = ScriptFileResolver.NormalizeFilePathForUri(
            request.TextDocument.Uri.ToUri().LocalPath);
        string docUriStr = request.TextDocument.Uri.ToString();

        var lenses = new List<CodeLens>();

        foreach (var kv in script.DefinitionsTable.GetAllFunctionLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fp = ScriptFileResolver.NormalizeFilePathForUri(kv.Value.FilePath ?? "");
            if (!string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase)) continue;

            lenses.Add(new CodeLens
            {
                Range = kv.Value.Range.ToRange(),
                Data  = BuildData(kv.Key.Qualifier, kv.Key.SymbolName, KindFunction, docUriStr)
            });
        }

        foreach (var kv in script.DefinitionsTable.GetAllClassLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fp = ScriptFileResolver.NormalizeFilePathForUri(kv.Value.FilePath ?? "");
            if (!string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase)) continue;

            lenses.Add(new CodeLens
            {
                Range = kv.Value.Range.ToRange(),
                Data  = BuildData(kv.Key.Qualifier, kv.Key.SymbolName, KindClass, docUriStr)
            });
        }

        return new CodeLensContainer(lenses);
    }

    // -------------------------------------------------------------------------
    // Pass 2 — fill in the reference count and wire up the command
    // -------------------------------------------------------------------------
    public override async Task<CodeLens> Handle(CodeLens request, CancellationToken cancellationToken)
    {
        if (request.Data is not JObject data)
            return Unresolved(request);

        string? ns   = data[DataNs]?.Value<string>();
        string? name = data[DataName]?.Value<string>();
        string? kind = data[DataKind]?.Value<string>();
        string? uri  = data[DataUri]?.Value<string>();

        if (ns is null || name is null || kind is null || uri is null)
            return Unresolved(request);

        // Build the SymbolKeys to match against the References dictionary.
        // Functions and methods share the same lookup key type; classes use their own.
        var keys = kind == KindFunction
            ? new[]
            {
                new SymbolKey(GSCode.Parser.SA.SymbolKind.Function, ns, name),
                new SymbolKey(GSCode.Parser.SA.SymbolKind.Method,   ns, name)
            }
            : new[]
            {
                new SymbolKey(GSCode.Parser.SA.SymbolKind.Class, ns, name)
            };

        // Count how many call/use sites exist across the whole workspace,
        // restricted to the same languageId as the declaring script.
        Script? originScript = scriptManager.GetParsedEditor(new Uri(uri));
        string? languageId = originScript?.LanguageId;

        int count = 0;
        foreach (var loaded in scriptManager.GetLoadedScripts())
        {
            if (languageId is not null &&
                !string.Equals(loaded.Script.LanguageId, languageId, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var key in keys)
            {
                if (loaded.Script.References.TryGetValue(key, out var ranges))
                    count += ranges.Count;
            }
        }

        // The declaration itself may appear in the References list of the declaring file
        // (e.g. a function token is recorded both as a definition and a call-site reference).
        // Subtract it so the lens shows only actual usages, not the function counting itself.
        if (originScript?.DefinitionsTable is not null)
        {
            foreach (var key in keys)
            {
                var declLoc = originScript.DefinitionsTable.GetSymbolLocation(key.Namespace, key.Name);
                if (declLoc is null) continue;

                Range declRange = declLoc.Value.Range.ToRange();
                if (originScript.References.TryGetValue(key, out var selfRanges) &&
                    selfRanges.Any(r => r == declRange))
                {
                    count--;
                    break; // only one declaration can match
                }
            }
        }

        string label = count == 1 ? "1 reference" : $"{count} references";

        // Clicking the lens triggers VS Code's built-in references view via
        // editor.action.showReferences. We pass an empty locations array; VS Code
        // calls textDocument/references itself to fill the panel.
        return request with
        {
            Command = new Command
            {
                Title     = label,
                Name      = "editor.action.showReferences",
                Arguments = new JArray
                {
                    uri,
                    JObject.FromObject(new
                    {
                        line      = request.Range.Start.Line,
                        character = request.Range.Start.Character
                    }),
                    new JArray()
                }
            }
        };
    }

    protected override CodeLensRegistrationOptions CreateRegistrationOptions(
        CodeLensCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = documentSelector,
            ResolveProvider  = true
        };

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private static JObject BuildData(string ns, string name, string kind, string uri)
        => new()
        {
            [DataNs]   = ns,
            [DataName] = name,
            [DataKind] = kind,
            [DataUri]  = uri
        };

    private static CodeLens Unresolved(CodeLens request)
        => request with { Command = new Command { Title = "0 references", Name = "" } };
}
