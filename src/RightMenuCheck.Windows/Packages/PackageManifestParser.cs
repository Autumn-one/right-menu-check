using System.Xml;
using System.Xml.Linq;
using RightMenuCheck.Core.Inventory;

namespace RightMenuCheck.Windows.Packages;

public sealed record PackageManifestVerb(
    string ApplicationId,
    string ItemType,
    string VerbId,
    string HandlerClsid,
    int? LineNumber);

public sealed record PackageManifestParseIssue(
    string ElementName,
    int? LineNumber,
    string Message);

public sealed record PackageManifestParseResult(
    IReadOnlyList<PackageManifestVerb> Verbs,
    IReadOnlyList<PackageManifestParseIssue> Issues);

public static class PackageManifestParser
{
    private const long MaximumManifestCharacters = 32L * 1024 * 1024;

    public static PackageManifestParseResult Parse(Stream manifestStream)
    {
        ArgumentNullException.ThrowIfNull(manifestStream);

        var settings = new XmlReaderSettings
        {
            CloseInput = false,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = MaximumManifestCharacters,
            XmlResolver = null,
        };

        using var reader = XmlReader.Create(manifestStream, settings);
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        var verbs = new List<PackageManifestVerb>();
        var issues = new List<PackageManifestParseIssue>();

        foreach (var extension in document
                     .Descendants()
                     .Where(IsFileExplorerContextMenuExtension))
        {
            var applicationId = extension
                .Ancestors()
                .FirstOrDefault(static element =>
                    element.Name.LocalName.Equals("Application", StringComparison.Ordinal))?
                .Attribute("Id")?
                .Value ?? string.Empty;

            foreach (var itemTypeElement in extension.Descendants().Where(static element =>
                         element.Name.LocalName.Equals("ItemType", StringComparison.Ordinal)))
            {
                var itemType = itemTypeElement.Attribute("Type")?.Value;
                if (string.IsNullOrWhiteSpace(itemType))
                {
                    issues.Add(CreateIssue(itemTypeElement, "ItemType", "Missing Type attribute."));
                    continue;
                }

                foreach (var verbElement in itemTypeElement.Elements().Where(static element =>
                             element.Name.LocalName.Equals("Verb", StringComparison.Ordinal)))
                {
                    var verbId = verbElement.Attribute("Id")?.Value;
                    var rawClsid = verbElement.Attribute("Clsid")?.Value;
                    var handlerClsid = ClsidUtilities.Normalize(rawClsid);

                    if (string.IsNullOrWhiteSpace(verbId))
                    {
                        issues.Add(CreateIssue(verbElement, "Verb", "Missing Id attribute."));
                        continue;
                    }

                    if (handlerClsid is null)
                    {
                        issues.Add(CreateIssue(verbElement, "Verb", "Missing or invalid Clsid attribute."));
                        continue;
                    }

                    verbs.Add(new PackageManifestVerb(
                        applicationId,
                        itemType,
                        verbId,
                        handlerClsid,
                        GetLineNumber(verbElement)));
                }
            }
        }

        return new PackageManifestParseResult(verbs, issues);
    }

    private static bool IsFileExplorerContextMenuExtension(XElement element) =>
        element.Name.LocalName.Equals("Extension", StringComparison.Ordinal) &&
        element.Attribute("Category")?.Value.Equals(
            "windows.fileExplorerContextMenus",
            StringComparison.OrdinalIgnoreCase) == true;

    private static PackageManifestParseIssue CreateIssue(
        XElement element,
        string elementName,
        string message) =>
        new(elementName, GetLineNumber(element), message);

    private static int? GetLineNumber(XElement element)
    {
        if (element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo())
        {
            return lineInfo.LineNumber;
        }

        return null;
    }
}
