using GSCode.Data.Helpers;

namespace GSCode.Data.Models;

public record class ScrMember
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public string? DocComment { get; set; }

    private string? _cachedDocumentation = null;

    /// <summary>
    /// Lazily builds and caches the markdown hover string for this member.
    /// </summary>
    public string Documentation
    {
        get
        {
            if (_cachedDocumentation is string cached) return cached;

            if (!string.IsNullOrWhiteSpace(DocComment))
                return _cachedDocumentation = ScriptDocCommentFormatter.FormatToMarkdown(DocComment, null);

            return _cachedDocumentation = !string.IsNullOrWhiteSpace(Description)
                ? Description
                : $"```gsc\n{Name}\n```";
        }
    }
}
