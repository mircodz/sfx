using Sfx.Core;

namespace Sfx.Tests;

public class ClusterEndpointTests
{
    [Theory]
    [InlineData("localhost", false, "http://localhost:19080/")]
    [InlineData("localhost", true, "https://localhost:19080/")]
    [InlineData("mycluster:19000", true, "https://mycluster:19000/")]
    [InlineData("https://mycluster.westus.cloudapp.azure.com", false, "https://mycluster.westus.cloudapp.azure.com:19080/")]
    [InlineData("https://mycluster:443/explorer", false, "https://mycluster/")]
    [InlineData("[::1]", false, "http://[::1]:19080/")]
    [InlineData("[::1]:1234", false, "http://[::1]:1234/")]
    public void Parse_fills_in_scheme_and_port(string value, bool secure, string expected)
    {
        Assert.Equal(expected, ClusterEndpoint.Parse(value, secure).ToString());
    }

    [Theory]
    [InlineData("ftp://host")]
    [InlineData("http://")]
    public void Parse_rejects_invalid_endpoints(string value)
    {
        Assert.Throws<FormatException>(() => ClusterEndpoint.Parse(value, secure: false));
    }
}
