using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Parser.Data;

public interface ISenseDefinition : ISemanticToken, IHoverable
{
}

public interface ISemanticToken
{
    public Range Range { get; }

    public string SemanticTokenType { get; }
    public string[] SemanticTokenModifiers { get; }
}

public class SemanticTokenDefinition(Range range, string semanticTokenType, string[] semanticTokenModifiers) : ISemanticToken
{
    public Range Range { get; } = range;
    public string SemanticTokenType { get; } = semanticTokenType;
    public string[] SemanticTokenModifiers { get; } = semanticTokenModifiers;
}

public interface IHoverable
{
    /// <summary>
    /// The range for this token. The range must not span over multiple lines.
    /// </summary>
    public Range Range { get; }

    public Hover GetHover();

}