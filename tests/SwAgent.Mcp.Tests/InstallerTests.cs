using System.IO;
using SwAgent.Contracts;
using SwAgent.Mcp.Install;
using SwAgent.Mcp.Registration;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class InstallerTests
{
    const string Version = "0.1.0-test";

    static string AddinIn(string directory) => Path.Combine(directory, "addin", AddInIdentity.AssemblyFileName);

    [Fact]
    public void Install_copies_the_release_to_its_version_folder_unblocks_it_and_registers_that_copy()
    {
        using var release = new FakeRelease();
        using var home = new ScratchDirectory();
        using var registry = new ScratchRegistry();
        var layout = new InstallLayout(home.Path);

        var result = new Installer(layout, new AddinRegistrar(registry.Root)).Install(release.Path, Version);

        string target = layout.VersionDirectory(Version);
        Assert.Equal(Version, result.Version);
        Assert.Equal(target, result.Directory);
        Assert.Equal(Path.Combine(target, "swmcp.exe"), result.Swmcp);
        Assert.True(result.Copied);
        Assert.Equal(FakeRelease.Files, result.FilesCopied);
        Assert.Equal(FakeRelease.Files, result.MarksRemoved);
        Assert.True(File.Exists(Path.Combine(target, "System.Runtime.dll")));
        Assert.False(MarkOfTheWeb.Has(AddinIn(target)), "the installed add-in is unblocked");
        Assert.True(MarkOfTheWeb.Has(Path.Combine(release.Path, "swmcp.exe")), "the download itself is left as it was");
        var status = new AddinRegistrar(registry.Root).Status();
        Assert.True(status.Registered);
        Assert.Equal(AddinIn(target), status.Path);
        Assert.Equal(Version, File.ReadAllText(layout.CurrentFile).Trim());
    }

    [Fact]
    public void Installing_the_same_version_again_replaces_its_files()
    {
        using var release = new FakeRelease();
        using var home = new ScratchDirectory();
        using var registry = new ScratchRegistry();
        var installer = new Installer(new InstallLayout(home.Path), new AddinRegistrar(registry.Root));
        installer.Install(release.Path, Version);
        File.WriteAllText(Path.Combine(release.Path, "System.Runtime.dll"), "newer");

        var again = installer.Install(release.Path, Version);

        Assert.Equal(FakeRelease.Files, again.FilesCopied);
        Assert.Equal("newer", File.ReadAllText(Path.Combine(again.Directory, "System.Runtime.dll")));
        Assert.True(new AddinRegistrar(registry.Root).Status().Registered);
    }

    [Fact]
    public void Install_run_from_the_installed_folder_copies_nothing_and_registers_it()
    {
        using var release = new FakeRelease();
        using var home = new ScratchDirectory();
        using var registry = new ScratchRegistry();
        var layout = new InstallLayout(home.Path);
        var installer = new Installer(layout, new AddinRegistrar(registry.Root));
        installer.Install(release.Path, Version);
        new AddinRegistrar(registry.Root).Unregister();

        var again = installer.Install(layout.VersionDirectory(Version), Version);

        Assert.False(again.Copied);
        Assert.Equal(0, again.FilesCopied);
        Assert.Equal(0, again.MarksRemoved);
        Assert.True(new AddinRegistrar(registry.Root).Status().Registered);
    }

    [Fact]
    public void A_folder_that_is_not_a_release_is_refused_and_nothing_is_written()
    {
        using var empty = new ScratchDirectory();
        using var home = new ScratchDirectory();
        using var registry = new ScratchRegistry();
        var layout = new InstallLayout(home.Path);

        var e = Assert.Throws<SwAgentException>(() => new Installer(layout, new AddinRegistrar(registry.Root)).Install(empty.Path, Version));

        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("swmcp.exe", e.Message);
        Assert.False(Directory.Exists(layout.Root));
        Assert.False(new AddinRegistrar(registry.Root).Status().ClsidKey);
    }

    [Fact]
    public void A_release_unzipped_into_the_install_root_is_refused()
    {
        using var home = new ScratchDirectory();
        using var registry = new ScratchRegistry();
        var layout = new InstallLayout(home.Path);
        Directory.CreateDirectory(layout.Root);
        using var release = new FakeRelease(layout.Root);

        var e = Assert.Throws<SwAgentException>(() => new Installer(layout, new AddinRegistrar(registry.Root)).Install(layout.Root, Version));

        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("unzip it somewhere else", e.Message);
        Assert.False(Directory.Exists(layout.VersionDirectory(Version)));
    }

    [Fact]
    public void A_file_held_open_in_the_version_folder_fails_with_what_to_do()
    {
        using var release = new FakeRelease();
        using var home = new ScratchDirectory();
        using var registry = new ScratchRegistry();
        var layout = new InstallLayout(home.Path);
        var installer = new Installer(layout, new AddinRegistrar(registry.Root));
        installer.Install(release.Path, Version);
        string held = AddinIn(layout.VersionDirectory(Version));

        SwAgentException e;
        using (File.Open(held, FileMode.Open, FileAccess.Read, FileShare.Read)) // as a SOLIDWORKS that loaded it does
            e = Assert.Throws<SwAgentException>(() => installer.Install(release.Path, Version));

        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("close SOLIDWORKS", e.Message);
        Assert.Equal(held, e.Error.Details!["file"]);
    }

    [Fact]
    public void Uninstall_unregisters_and_removes_every_version_but_the_one_it_runs_from()
    {
        using var release = new FakeRelease();
        using var home = new ScratchDirectory();
        using var registry = new ScratchRegistry();
        var layout = new InstallLayout(home.Path);
        var installer = new Installer(layout, new AddinRegistrar(registry.Root));
        installer.Install(release.Path, "0.1.0");
        installer.Install(release.Path, "0.2.0");

        var result = installer.Uninstall(layout.VersionDirectory("0.2.0"));

        Assert.Equal(4, result.RemovedKeys.Count);
        Assert.Equal(new[] { layout.VersionDirectory("0.1.0") }, result.Removed);
        var kept = Assert.Single(result.Kept);
        Assert.Equal(layout.VersionDirectory("0.2.0"), kept.Directory);
        Assert.Contains("running from it", kept.Reason);
        Assert.False(Directory.Exists(layout.VersionDirectory("0.1.0")));
        Assert.True(Directory.Exists(layout.VersionDirectory("0.2.0")));
        Assert.False(File.Exists(layout.CurrentFile));
        Assert.False(new AddinRegistrar(registry.Root).Status().ClsidKey);
    }

    [Fact]
    public void Uninstall_run_from_the_download_removes_every_version()
    {
        using var release = new FakeRelease();
        using var home = new ScratchDirectory();
        using var registry = new ScratchRegistry();
        var layout = new InstallLayout(home.Path);
        var installer = new Installer(layout, new AddinRegistrar(registry.Root));
        installer.Install(release.Path, Version);

        var result = installer.Uninstall(release.Path);

        Assert.Equal(new[] { layout.VersionDirectory(Version) }, result.Removed);
        Assert.Empty(result.Kept);
        Assert.Empty(layout.InstalledVersionDirectories());
    }

    [Fact]
    public void A_version_folder_in_use_is_kept_with_the_reason()
    {
        using var release = new FakeRelease();
        using var home = new ScratchDirectory();
        using var registry = new ScratchRegistry();
        var layout = new InstallLayout(home.Path);
        var installer = new Installer(layout, new AddinRegistrar(registry.Root));
        installer.Install(release.Path, Version);

        UninstallResult result;
        using (File.Open(AddinIn(layout.VersionDirectory(Version)), FileMode.Open, FileAccess.Read, FileShare.Read))
            result = installer.Uninstall(release.Path);

        Assert.Empty(result.Removed);
        var kept = Assert.Single(result.Kept);
        Assert.Equal(layout.VersionDirectory(Version), kept.Directory);
        Assert.Contains("close SOLIDWORKS", kept.Reason);
        Assert.Equal(4, result.RemovedKeys.Count);
    }

    [Fact]
    public void Uninstall_with_nothing_installed_does_nothing_and_says_so()
    {
        using var home = new ScratchDirectory();
        using var registry = new ScratchRegistry();
        var result = new Installer(new InstallLayout(home.Path), new AddinRegistrar(registry.Root)).Uninstall(home.Path);
        Assert.Empty(result.RemovedKeys);
        Assert.Empty(result.Removed);
        Assert.Empty(result.Kept);
    }
}
