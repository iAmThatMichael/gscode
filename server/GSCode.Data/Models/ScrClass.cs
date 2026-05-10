using GSCode.Data.Models.Interfaces;

namespace GSCode.Data.Models;

public record class ScrClass : IExportedSymbol
{
    public string Name { get; init; } = default!;
    public string? Description { get; init; }
    public string? InheritsFrom { get; set; }

    // TODO: members are only scoped to functions that occur after their definition.
    private List<ScrFunction>? _methods;
    public List<ScrFunction> Methods { get => _methods ??= []; set => _methods = value; }

    private List<ScrMember>? _members;
    public List<ScrMember> Members { get => _members ??= []; set => _members = value; }

    public ExportedSymbolType Type { get; init; } = ExportedSymbolType.Class;
}
