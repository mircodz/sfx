using System.Globalization;
using System.Xml.Linq;

namespace Sfx.Core;

/// <summary>
/// The node capacities a cluster manifest declares, per node type and metric. The load queries report a metric without a
/// capacity as 0 (or a huge number), the same as one declared as 0, which means nothing with that load may be placed;
/// only the manifest tells the two apart.
/// </summary>
public sealed class NodeCapacities(IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> byNodeType)
{
    public static readonly NodeCapacities None = new(new Dictionary<string, IReadOnlyDictionary<string, double>>());

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> ByNodeType => byNodeType;

    public static NodeCapacities Parse(string clusterManifest)
    {
        var nodeTypes = XDocument.Parse(clusterManifest).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "NodeTypes");
        return new NodeCapacities((nodeTypes?.Elements() ?? [])
            .Where(t => t.Name.LocalName == "NodeType" && !string.IsNullOrEmpty((string?)t.Attribute("Name")))
            .GroupBy(t => (string)t.Attribute("Name")!)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, double>)g.First().Elements()
                    .Where(e => e.Name.LocalName == "Capacities")
                    .SelectMany(e => e.Elements())
                    .Where(c => c.Name.LocalName == "Capacity" && !string.IsNullOrEmpty((string?)c.Attribute("Name"))
                        && double.TryParse((string?)c.Attribute("Value"), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    .GroupBy(c => (string)c.Attribute("Name")!)
                    .ToDictionary(c => c.Key, c => double.Parse((string)c.Last().Attribute("Value")!, NumberStyles.Float, CultureInfo.InvariantCulture))));
    }

    /// <summary>Whether nodes of <paramref name="nodeType"/> declare a capacity for <paramref name="metric"/>.</summary>
    public bool Declares(string nodeType, string metric) => byNodeType.TryGetValue(nodeType, out var metrics) && metrics.ContainsKey(metric);

    /// <summary>Whether every node type declares a capacity for <paramref name="metric"/>, so the cluster's total is a real limit.</summary>
    public bool DeclaredEverywhere(string metric) => byNodeType.Count > 0 && byNodeType.Values.All(m => m.ContainsKey(metric));
}
