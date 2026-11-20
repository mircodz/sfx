using Sfx.Core;
using Sfx.Web;

namespace Sfx.Tests;

public class CapacityTests
{
    private const string Manifest = """
        <ClusterManifest xmlns="http://schemas.microsoft.com/2011/01/fabric" Name="test" Version="1.0">
          <NodeTypes>
            <NodeType Name="FrontEnd">
              <Capacities>
                <Capacity Name="MemoryMB" Value="0" />
                <Capacity Name="Cpu" Value="8" />
              </Capacities>
            </NodeType>
            <NodeType Name="BackEnd">
              <Capacities>
                <Capacity Name="Cpu" Value="16" />
              </Capacities>
            </NodeType>
          </NodeTypes>
        </ClusterManifest>
        """;

    [Fact]
    public void Reads_declared_capacities_per_node_type()
    {
        var capacities = NodeCapacities.Parse(Manifest);

        Assert.True(capacities.Declares("FrontEnd", "MemoryMB"));
        Assert.False(capacities.Declares("BackEnd", "MemoryMB"));
        Assert.False(capacities.Declares("Other", "Cpu"));
        Assert.True(capacities.DeclaredEverywhere("Cpu"));
        Assert.False(capacities.DeclaredEverywhere("MemoryMB"));
        Assert.False(NodeCapacities.None.DeclaredEverywhere("Cpu"));
        Assert.Empty(NodeCapacities.Parse("<ClusterManifest />").ByNodeType);
    }

    [Theory]
    [InlineData("8", false, 8.0)]
    [InlineData("0", false, null)]
    [InlineData("0", true, 0.0)]
    [InlineData("-1", true, null)]
    [InlineData("1e15", true, null)]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    public void Zero_is_a_limit_only_when_declared(string? value, bool declared, double? expected) =>
        Assert.Equal(expected, Api.Capacity(value, declared));
}
