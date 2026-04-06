using Newtonsoft.Json;

namespace GSCode.Data.Models;

public sealed class ScrFunctionOverload
{
    /// <summary>The entity that this function is called on.</summary>
    public ScrFunctionArg? CalledOn { get; set; }

    /// <summary>The parameter list of this function, which may be empty.</summary>
    public List<ScrFunctionArg> Parameters { get; set; } = [];

    /// <summary>The return value of this function, if any.</summary>
    public ScrFunctionReturn? Returns { get; set; }

    /// <summary>Whether this function accepts variable arguments (...).</summary>
    public bool Vararg { get; set; } = false;
}

public record class ScrFunctionReturn
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public ScrFunctionDataType? Type { get; set; }
    public bool? Void { get; set; }
}

public record class ScrFunctionArg
{
    // TODO: nullable due to issues within the API — enforce not-null once API is clean
    /// <summary>The name of the parameter.</summary>
    public string Name { get; set; } = default!;

    /// <summary>The description for this parameter.</summary>
    public string? Description { get; set; }

    /// <summary>The type of the parameter.</summary>
    public ScrFunctionDataType? Type { get; set; }

    /// <summary>Whether the parameter is mandatory or optional.</summary>
    public bool? Mandatory { get; set; }

    /// <summary>The default value for this parameter.</summary>
    public ScriptValue? Default { get; set; }
}

public record class ScrFunctionDataType
{
    public string DataType { get; set; } = default!;
    public string? InstanceType { get; set; }
    public bool IsArray { get; set; } = false;
}
