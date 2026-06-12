using GSCode.Data.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GSCode.Data.Models.Interfaces;

[JsonDerivedType(typeof(ScrFunction), typeDiscriminator: "function")]
[JsonDerivedType(typeof(ScrClass), typeDiscriminator: "class")]
public interface IExportedSymbol
{
    public string Name { get; init; }
    public string? Description { get; init; }
    public ExportedSymbolType Type { get; init; }
}

public enum ExportedSymbolType
{
    Function,
    Class
}
