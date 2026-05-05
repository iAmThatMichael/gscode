using GSCode.Parser;

namespace GSCode.NET.LSP;

/// <summary>
/// A lightweight snapshot of a loaded script and its URI.
/// Returned by <see cref="ScriptManager.GetLoadedScripts"/> for cross-file operations
/// such as workspace-wide reference search.
/// </summary>
public readonly record struct LoadedScript(Uri Uri, Script Script);
