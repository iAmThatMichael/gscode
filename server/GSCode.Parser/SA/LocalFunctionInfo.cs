using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.SA;

/// <summary>
/// Rich parameter metadata for a single function parameter.
/// </summary>
public sealed record FunctionParameter(string Name, bool ByRef, bool HasDefault);

/// <summary>
/// Local variable metadata within a function's scope.
/// </summary>
public sealed record FunctionLocal(
    string Name,
    Range SourceLocation,
    bool IsConst
);

/// <summary>
/// Field assignment metadata within a function.
/// </summary>
public sealed record FunctionFieldAssignment(
    string OwnerName,
    string FieldName,
    Range SourceLocation
);

/// <summary>
/// Complete consolidated metadata for a function or method.
/// Seeded by SignatureAnalyser; Variables/FieldAssignments merged by TypeFlowAnalyser.
/// </summary>
public sealed record CompleteFunctionDefinition
{
    // ========== IDENTITY ==========
    public required string Name { get; init; }
    public required string Namespace { get; init; }
    public string? ParentClass { get; init; }

    // ========== SIGNATURE & METADATA ==========
    public FunctionParameter[] Parameters { get; init; } = [];
    public string[] Flags { get; init; } = [];
    public string? DocComment { get; init; }
    public string? Description { get; init; }
    public string? Example { get; init; }
    public string? Confidence { get; init; }

    // ========== SOURCE LOCATION ==========
    public required string LocalScriptPath { get; init; }
    public required Range SourceRange { get; init; }
    public int BodyEndLine { get; init; }

    // ========== ANALYSIS DATA ==========
    public FunctionLocal[] Variables { get; init; } = [];
    public FunctionFieldAssignment[] FieldAssignments { get; init; } = [];
}

/// <summary>
/// Member declaration metadata within a class definition.
/// </summary>
public sealed record ClassMember(string Name, string? DocComment, Range SourceRange);

/// <summary>
/// Complete consolidated metadata for a class definition.
/// Seeded by SignatureAnalyser.
/// </summary>
public sealed record CompleteClassDefinition
{
    // ========== IDENTITY ==========
    public required string Name { get; init; }
    public required string Namespace { get; init; }
    public string? InheritsFrom { get; init; }

    // ========== METADATA ==========
    public string? DocComment { get; init; }
    public ClassMember[] Members { get; init; } = [];

    // ========== SOURCE LOCATION ==========
    public required string LocalScriptPath { get; init; }
    public required Range SourceRange { get; init; }
    public int BodyEndLine { get; init; }
}
