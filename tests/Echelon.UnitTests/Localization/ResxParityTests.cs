using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Echelon.UnitTests.Localization;

/// <summary>
/// Guards the PWA's three hand-synced localization files against drift.
/// </summary>
/// <remarks>
/// The Designer is not regenerated at build (VS's PublicResXFileCodeGenerator runs in the IDE only),
/// so the neutral resx, the <c>.ru</c> resx, and <c>UiStrings.Designer.cs</c> are edited together by
/// hand. Three failure modes slip past the compiler: a neutral key with no Russian translation (the
/// UI silently falls back to English), a Designer property whose resx key was never added (a null at
/// render, an NRE), and a resx key no property exposes (dead, but a sign the sync missed a file). The
/// compiler only catches a razor referencing a property the Designer lacks. This closes the rest.
/// </remarks>
public class ResxParityTests
{
    private static readonly string ResourcesDir = LocateResourcesDir();

    [Fact]
    public void NeutralAndRussian_HaveTheSameKeys()
    {
        var en = ResxKeys("UiStrings.resx");
        var ru = ResxKeys("UiStrings.ru.resx");

        var missingRu = en.Except(ru).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extraRu = ru.Except(en).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(missingRu.Count == 0, $"Keys without a Russian translation: {string.Join(", ", missingRu)}");
        Assert.True(extraRu.Count == 0, $"Russian keys absent from the neutral resx: {string.Join(", ", extraRu)}");
    }

    [Fact]
    public void EveryDesignerProperty_HasANeutralKey()
    {
        var en = ResxKeys("UiStrings.resx");
        var properties = DesignerProperties();

        var orphaned = properties.Except(en).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(orphaned.Count == 0,
            $"Designer properties with no resx key (null at runtime): {string.Join(", ", orphaned)}");
    }

    [Fact]
    public void EveryNeutralKey_HasADesignerProperty()
    {
        var en = ResxKeys("UiStrings.resx");
        var properties = DesignerProperties();

        var unexposed = en.Except(properties).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(unexposed.Count == 0,
            $"resx keys no Designer property exposes (sync missed the Designer): {string.Join(", ", unexposed)}");
    }

    [Fact]
    public void NoValueIsBlank()
    {
        foreach (var file in new[] { "UiStrings.resx", "UiStrings.ru.resx" })
        {
            var blank = DataElements(file)
                .Where(d => string.IsNullOrWhiteSpace((string?)d.Element("value")))
                .Select(d => (string?)d.Attribute("name"))
                .ToList();
            Assert.True(blank.Count == 0, $"{file} has blank values for: {string.Join(", ", blank)}");
        }
    }

    private static IEnumerable<XElement> DataElements(string fileName) =>
        XDocument.Load(Path.Combine(ResourcesDir, fileName)).Root!.Elements("data");

    private static HashSet<string> ResxKeys(string fileName) =>
        DataElements(fileName)
            .Select(d => (string?)d.Attribute("name"))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> DesignerProperties()
    {
        var text = File.ReadAllText(Path.Combine(ResourcesDir, "UiStrings.Designer.cs"));
        return Regex.Matches(text, @"public static string (\w+) \{")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Walks up from the test assembly to the repo root (the folder with the slnx) and into the PWA resources.</summary>
    private static string LocateResourcesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("Echelon.slnx").Any())
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the repository root (Echelon.slnx) from the test assembly.");
        return Path.Combine(dir!.FullName, "src", "Echelon.Pwa", "Resources");
    }
}
