using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Sfx.Core;

/// <summary>
/// An application package on this machine, checked and ready to upload: a package folder as is, or an .sfpkg (or
/// .zip) extracted to a temporary folder that <see cref="Dispose"/> removes.
/// </summary>
public sealed class LocalPackage : IDisposable
{
    public const string ManifestFile = "ApplicationManifest.xml";

    private readonly string? _temporary;

    private LocalPackage(string folder, string typeName, string typeVersion, string? temporary)
    {
        Folder = folder;
        TypeName = typeName;
        TypeVersion = typeVersion;
        _temporary = temporary;
    }

    /// <summary>The package's folder: the one given, or where the file was extracted.</summary>
    public string Folder { get; }

    public string TypeName { get; }

    public string TypeVersion { get; }

    /// <summary>Where it goes in the image store unless told otherwise: "{type}_{version}", so versions don't overwrite each other.</summary>
    public string StorePath => ImageStore.SafeName($"{TypeName}_{TypeVersion}");

    /// <summary>
    /// Opens a package folder, or extracts an .sfpkg or .zip file first, and reads its type and version from the
    /// ApplicationManifest.xml at its root. Throws a <see cref="RefusedException"/> saying what's wrong when it isn't a package.
    /// </summary>
    public static LocalPackage Open(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        if (Directory.Exists(full))
        {
            var folder = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var (name, version) = ReadManifestIn(folder, folder);
            return new LocalPackage(folder, name, version, null);
        }
        if (!File.Exists(full))
        {
            throw new RefusedException($"There's no folder or file {full}.");
        }

        var temporary = Directory.CreateTempSubdirectory("sfx-package-").FullName;
        try
        {
            try
            {
                ZipFile.ExtractToDirectory(full, temporary);
            }
            catch (InvalidDataException ex)
            {
                throw new RefusedException($"{Path.GetFileName(full)} isn't a package folder, an .sfpkg or a .zip file.", inner: ex);
            }
            catch (IOException ex)
            {
                // Among others, an entry that would land outside the folder it's extracted to.
                throw new RefusedException($"{Path.GetFileName(full)} can't be extracted: {ex.Message}", inner: ex);
            }
            var (name, version) = ReadManifestIn(temporary, full);
            return new LocalPackage(temporary, name, version, temporary);
        }
        catch
        {
            Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    /// <summary>The type name and version an application manifest declares; a <see cref="RefusedException"/> when it doesn't.</summary>
    /// <param name="source">Where the manifest came from, for the message.</param>
    public static (string TypeName, string TypeVersion) ReadManifest(string manifest, string source)
    {
        XElement? root;
        try
        {
            root = XDocument.Parse(manifest).Root;
        }
        catch (XmlException ex)
        {
            throw new RefusedException($"The {ManifestFile} in {source} isn't valid XML: {ex.Message}", inner: ex);
        }
        var name = root?.Attribute("ApplicationTypeName")?.Value;
        var version = root?.Attribute("ApplicationTypeVersion")?.Value;
        if (root?.Name.LocalName != "ApplicationManifest" || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
        {
            throw new RefusedException(
                $"The {ManifestFile} in {source} isn't an application manifest: it needs an ApplicationManifest element with ApplicationTypeName and ApplicationTypeVersion.");
        }
        return (name, version);
    }

    private static (string, string) ReadManifestIn(string folder, string source)
    {
        var manifest = Path.Combine(folder, ManifestFile);
        if (!File.Exists(manifest))
        {
            // A common slip: zipping the package folder rather than what's in it.
            var nested = Directory.EnumerateDirectories(folder).FirstOrDefault(d => File.Exists(Path.Combine(d, ManifestFile)));
            throw new RefusedException(nested is null
                ? $"{source} has no {ManifestFile} at its root, so it isn't an application package."
                : $"{source} has no {ManifestFile} at its root, but {Path.GetFileName(nested)}/ has one: use that folder, or package what's inside it.");
        }
        return ReadManifest(File.ReadAllText(manifest), source);
    }

    public void Dispose()
    {
        if (_temporary is not null && Directory.Exists(_temporary))
        {
            Directory.Delete(_temporary, recursive: true);
        }
    }
}
