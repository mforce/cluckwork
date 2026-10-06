using System.Xml.Linq;

namespace Cluckwork.Api.IntegrationTests;

public sealed class SolutionItemTests
{
    [Fact]
    public void EveryListedSolutionItem_Exists()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Cluckwork.slnx")))
            root = root.Parent;

        Assert.NotNull(root);
        var items = XDocument.Load(Path.Combine(root.FullName, "Cluckwork.slnx"))
            .Descendants("File")
            .Select(item => item.Attribute("Path")?.Value
                ?? throw new InvalidDataException("A solution item has no Path attribute."))
            .ToArray();

        Assert.NotEmpty(items);
        Assert.All(items, path =>
            Assert.True(File.Exists(Path.Combine(root.FullName, path.Replace('\\', '/'))),
                $"Missing solution item: {path}"));
    }
}
