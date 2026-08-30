using System.Text;
using System.Xml;
using RightMenuCheck.Windows.Packages;

namespace RightMenuCheck.Windows.Tests.Packages;

public sealed class PackageManifestParserTests
{
    [Fact]
    public void ParseReadsDesktop4AndDesktop5ContextMenuElements()
    {
        using var stream = CreateStream(ValidManifest);

        var result = PackageManifestParser.Parse(stream);

        Assert.Equal(2, result.Verbs.Count);
        var fileVerb = Assert.Single(result.Verbs, verb => verb.ItemType == "*");
        var folderVerb = Assert.Single(
            result.Verbs,
            verb => verb.ItemType == "Directory\\Background");
        Assert.Equal("App", fileVerb.ApplicationId);
        Assert.Equal("Inspect", fileVerb.VerbId);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", fileVerb.HandlerClsid);
        Assert.Equal("OpenHere", folderVerb.VerbId);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ParseReportsInvalidVerbWithoutDroppingValidSiblings()
    {
        const string manifest = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
                     xmlns:desktop5="http://schemas.microsoft.com/appx/manifest/desktop/windows10/5">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <desktop4:Extension Category="windows.fileExplorerContextMenus">
                      <desktop4:FileExplorerContextMenus>
                        <desktop5:ItemType Type=".txt">
                          <desktop5:Verb Id="Valid" Clsid="{11111111-2222-3333-4444-555555555555}" />
                          <desktop5:Verb Id="Invalid" Clsid="not-a-guid" />
                        </desktop5:ItemType>
                      </desktop4:FileExplorerContextMenus>
                    </desktop4:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """;
        using var stream = CreateStream(manifest);

        var result = PackageManifestParser.Parse(stream);

        Assert.Single(result.Verbs);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("Verb", issue.ElementName);
        Assert.Contains("Clsid", issue.Message, StringComparison.Ordinal);
        Assert.NotNull(issue.LineNumber);
    }

    [Fact]
    public void ParseRejectsDocumentTypeDeclarations()
    {
        const string manifest = """
            <!DOCTYPE Package [<!ENTITY unsafe SYSTEM "file:///c:/windows/win.ini">]>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" />
            """;
        using var stream = CreateStream(manifest);

        Assert.Throws<XmlException>(() => PackageManifestParser.Parse(stream));
    }

    internal const string ValidManifest = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
                 xmlns:desktop5="http://schemas.microsoft.com/appx/manifest/desktop/windows10/5">
          <Applications>
            <Application Id="App">
              <Extensions>
                <desktop4:Extension Category="windows.fileExplorerContextMenus">
                  <desktop4:FileExplorerContextMenus>
                    <desktop5:ItemType Type="*">
                      <desktop5:Verb Id="Inspect" Clsid="11111111-2222-3333-4444-555555555555" />
                    </desktop5:ItemType>
                    <desktop5:ItemType Type="Directory\Background">
                      <desktop5:Verb Id="OpenHere" Clsid="AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE" />
                    </desktop5:ItemType>
                  </desktop4:FileExplorerContextMenus>
                </desktop4:Extension>
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """;

    private static MemoryStream CreateStream(string content) =>
        new(Encoding.UTF8.GetBytes(content), writable: false);
}
