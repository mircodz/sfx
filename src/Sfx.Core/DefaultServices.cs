using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sfx.Core;

/// <summary>
/// A parameter that an application manifest's default service uses, e.g. <c>[Counter_TargetReplicaSetSize]</c> in
/// the Counter service's <c>TargetReplicaSetSize</c>.
/// </summary>
/// <param name="Service">The default service's (or service group's) name, e.g. "Counter".</param>
/// <param name="Setting">The attribute that uses it, e.g. "TargetReplicaSetSize" or "PartitionCount".</param>
/// <param name="Partitioning">It sets how the service is partitioned, which can never change once the service exists.</param>
public sealed record DefaultServiceUse(string Parameter, string Service, string Setting, bool Partitioning);

/// <summary>
/// What an upgrade may change about default services. By default a cluster refuses application upgrades that change
/// one (aka.ms/upgrade-defaultservices); <c>ClusterManager/EnableDefaultServicesUpgrade</c> allows it, except for partitioning.
/// </summary>
public static partial class DefaultServices
{
    private static readonly HashSet<string> PartitionElements =
        ["UniformInt64Partition", "NamedPartition", "SingletonPartition"];

    /// <summary>Every use of a parameter by a default service or service group in an application manifest.</summary>
    public static IReadOnlyList<DefaultServiceUse> ParameterUses(string applicationManifest)
    {
        var root = XDocument.Parse(applicationManifest).Root;
        var section = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "DefaultServices");
        if (section is null)
        {
            return [];
        }

        var uses = new List<DefaultServiceUse>();
        foreach (var service in section.Elements().Where(e => e.Name.LocalName is "Service" or "ServiceGroup"))
        {
            var name = (string?)service.Attribute("Name") ?? "";
            foreach (var element in service.DescendantsAndSelf())
            {
                var partitioning = PartitionElements.Contains(element.Name.LocalName)
                    || element.Ancestors().Any(a => PartitionElements.Contains(a.Name.LocalName));
                foreach (var attribute in element.Attributes())
                {
                    foreach (Match m in ParameterReference().Matches(attribute.Value))
                    {
                        uses.Add(new DefaultServiceUse(m.Groups[1].Value, name, attribute.Name.LocalName, partitioning));
                    }
                }

                // Named partitions list their names as child elements' values or attributes; both are covered above,
                // but element text can reference parameters too.
                if (!element.HasElements && ParameterReference().Matches(element.Value) is { Count: > 0 } inText)
                {
                    uses.AddRange(inText.Select(m => new DefaultServiceUse(m.Groups[1].Value, name, element.Name.LocalName, partitioning)));
                }
            }
        }
        return uses.Distinct().ToList();
    }

    /// <summary>
    /// The parameters whose value the application has after an upgrade differs from the one it has now. A parameter
    /// the application doesn't set follows its version's default, so a default that changed between versions counts too.
    /// Parameters new in the target version are left out: nothing running uses them yet.
    /// </summary>
    /// <param name="current">What the application sets today.</param>
    /// <param name="currentDefaults">The defaults of the version it runs.</param>
    /// <param name="upgraded">What the upgrade sets.</param>
    /// <param name="targetDefaults">The defaults of the version it upgrades to; also every parameter that version has.</param>
    public static IReadOnlyList<string> ChangedParameters(
        IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> currentDefaults,
        IReadOnlyDictionary<string, string> upgraded, IReadOnlyDictionary<string, string> targetDefaults) =>
        targetDefaults.Keys
            .Where(name => (current.GetValueOrDefault(name) ?? currentDefaults.GetValueOrDefault(name)) is { } before
                && before != (upgraded.GetValueOrDefault(name) ?? targetDefaults[name]))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The DefaultServices section as written, to tell whether two versions declare them differently.</summary>
    public static string Declaration(string applicationManifest)
    {
        var section = XDocument.Parse(applicationManifest).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "DefaultServices");
        return section?.ToString(SaveOptions.DisableFormatting) ?? "";
    }

    /// <summary>Whether the cluster lets application upgrades change default services; off unless the manifest says so.</summary>
    public static bool UpgradeEnabled(string clusterManifest) =>
        ClusterSettings.Parse(clusterManifest)
            .Where(s => s.Name == "ClusterManager")
            .SelectMany(s => s.Parameters)
            .Any(p => p.Name == "EnableDefaultServicesUpgrade" && string.Equals(p.Value, "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>The error the cluster gives when an upgrade would change a default service it isn't allowed to.</summary>
    public static bool IsRefusal(Exception ex) =>
        ex.GetBaseException().Message.Contains("upgrade-defaultservices", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\[([A-Za-z_][\w.]*)\]")]
    private static partial Regex ParameterReference();
}
