using System.IO;
using SwAgent.Mcp.Install;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class MarkOfTheWebTests
{
    /// <summary>What a browser writes on a download, and Explorer's Extract All on every file it unzips from one.</summary>
    internal static void Mark(string file) => File.WriteAllText(file + ":" + MarkOfTheWeb.StreamName, "[ZoneTransfer]\r\nZoneId=3\r\n");

    [Fact]
    public void Every_marked_file_below_the_folder_is_unmarked_and_kept()
    {
        using var scratch = new ScratchDirectory();
        string exe = Path.Combine(scratch.Path, "swmcp.exe");
        string dll = Path.Combine(scratch.Path, "addin", "SwAgent.AddIn.dll");
        string readme = Path.Combine(scratch.Path, "README.md");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        foreach (var f in new[] { exe, dll, readme }) File.WriteAllText(f, "x");
        Mark(exe);
        Mark(dll);
        Assert.True(MarkOfTheWeb.Has(exe));
        Assert.False(MarkOfTheWeb.Has(readme));

        Assert.Equal(2, MarkOfTheWeb.RemoveAll(scratch.Path));
        Assert.False(MarkOfTheWeb.Has(exe) || MarkOfTheWeb.Has(dll) || MarkOfTheWeb.Has(readme));
        Assert.Equal("x", File.ReadAllText(dll));
        Assert.Equal(0, MarkOfTheWeb.RemoveAll(scratch.Path));
    }

    [Fact]
    public void A_copy_keeps_the_mark_so_the_installed_copy_has_to_be_unmarked()
    {
        using var scratch = new ScratchDirectory();
        string a = Path.Combine(scratch.Path, "a.dll");
        string b = Path.Combine(scratch.Path, "b.dll");
        File.WriteAllText(a, "x");
        Mark(a);
        File.Copy(a, b);
        Assert.True(MarkOfTheWeb.Has(b));
    }
}
