using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Data.Models.Interfaces;

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
