using System.Xml;
using System.Xml.Linq;

namespace Sfx.Core;

/// <summary>What kind of cluster a cluster manifest describes.</summary>
public static class ClusterKind
{
    /// <summary>
    /// Whether the cluster runs in Azure under the Service Fabric resource provider, which owns its upgrades: the
    /// manifest's Infrastructure is PaaS (standalone clusters have WindowsServer or Linux, dev clusters Blackbird and the like).
    /// </summary>
    public static bool IsAzureManaged(string clusterManifest)
    {
        try
        {
            var infrastructure = XDocument.Parse(clusterManifest).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Infrastructure");
            return infrastructure?.Elements().Any(e => e.Name.LocalName == "PaaS") ?? false;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
