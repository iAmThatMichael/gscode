using GSCode.Data.Models;
using GSCode.Parser.DFA;
using Xunit;

namespace GSCode.Tests;

public class ScrDataApiTypeTests
{
    [Fact]
    public void FromApiType_MapsUint64_ToUInt64()
    {
        ScrFunctionDataType apiType = new()
        {
            DataType = "uint64"
        };

        ScrData result = ScrData.FromApiType(apiType);

        Assert.True(result.HasType(ScrDataTypes.UInt64));
        Assert.Equal(ScrDataTypeNames.UInt64, result.TypeToString());
    }
}
