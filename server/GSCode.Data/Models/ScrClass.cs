using GSCode.Data.Models.Interfaces;

namespace GSCode.Data.Models;

public record class ScrClass : IExportedSymbol
{
    public string Name { get; init; } = default!;
    public string? Description { get; init; }
    public string? InheritsFrom { get; set; }

    // TODO: members are only scoped to functions that occur after their definition.
    public List<ScrFunction> Methods { get; set; } = [];
    public List<ScrMember> Members { get; set; } = [];

    public ExportedSymbolType Type { get; init; } = ExportedSymbolType.Class;
}
