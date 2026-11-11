using System;
using System.IO;
using SwAgent.Mcp.Install;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class InstallLayoutTests
{
    [Fact]
    public void Paths_are_under_solidworks_mcp_app()
    {
        var layout = new InstallLayout(@"C:\Users\u\AppData\Local");
        Assert.Equal(@"C:\Users\u\AppData\Local\solidworks-mcp\app", layout.Root);
        Assert.Equal(@"C:\Users\u\AppData\Local\solidworks-mcp\app\0.1.0", layout.VersionDirectory("0.1.0"));
        Assert.Equal(@"C:\Users\u\AppData\Local\solidworks-mcp\app\current.txt", layout.CurrentFile);
    }

    [Theory]
    [InlineData("0.1.0+6bd3bc82d1559fc215178a6d3259ef6be447d47e", "0.1.0")]
    [InlineData("0.1.0-dryrun.7+abc", "0.1.0-dryrun.7")]
    [InlineData("0.1.0", "0.1.0")]
    public void The_folder_is_the_version_without_its_commit(string informational, string folder) =>
        Assert.Equal(folder, InstallLayout.FolderName(informational));

    [Theory]
    [InlineData("")]
    [InlineData("+abc")]
    [InlineData("..")]
    [InlineData("0.1.0/evil")]
    public void A_version_that_is_not_a_folder_name_is_refused(string informational) =>
        Assert.Throws<ArgumentException>(() => InstallLayout.FolderName(informational));

    [Fact]
    public void Installed_versions_are_the_folders_under_the_root_only()
    {
        using var scratch = new ScratchDirectory();
        var layout = new InstallLayout(scratch.Path);
        Assert.Empty(layout.InstalledVersionDirectories());
        Directory.CreateDirectory(layout.VersionDirectory("0.2.0"));
        Directory.CreateDirectory(layout.VersionDirectory("0.1.0"));
        File.WriteAllText(layout.CurrentFile, "0.2.0");
        Assert.Equal(new[] { layout.VersionDirectory("0.1.0"), layout.VersionDirectory("0.2.0") }, layout.InstalledVersionDirectories());
    }

    [Fact]
    public void Folders_that_are_not_versions_are_not_installs()
    {
        // A build published straight into app\, as the README before 3.F said, leaves addin\ and runtimes\ there:
        // uninstall must not take them for version folders and delete them.
        using var scratch = new ScratchDirectory();
        var layout = new InstallLayout(scratch.Path);
        Directory.CreateDirectory(Path.Combine(layout.Root, "addin"));
        Directory.CreateDirectory(Path.Combine(layout.Root, "runtimes"));
        Directory.CreateDirectory(layout.VersionDirectory("0.1.0-dryrun.7"));
        Assert.Equal(new[] { layout.VersionDirectory("0.1.0-dryrun.7") }, layout.InstalledVersionDirectories());
    }

    [Fact]
    public void Directories_compare_without_case_or_trailing_separators()
    {
        Assert.True(InstallLayout.SameDirectory(@"C:\A\app\0.1.0\", @"c:\a\APP\0.1.0"));
        Assert.False(InstallLayout.SameDirectory(@"C:\A\app\0.1.0", @"C:\A\app\0.1.00"));
        Assert.True(InstallLayout.IsInside(@"C:\A\app\0.1.0", @"C:\A\app\"));
        Assert.False(InstallLayout.IsInside(@"C:\A\application", @"C:\A\app"));
    }
}
