using System;
using System.IO;
using Microsoft.Win32;
using SwAgent.Contracts;
using SwAgent.Mcp.Registration;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class RegistrarTests
{
    [Fact]
    public void Register_writes_the_COM_and_SOLIDWORKS_keys()
    {
        using var scratch = new ScratchRegistry();
        var assembly = new AddinRegistrar(scratch.Root).Register(Payload.AddinPath);
        string codeBase = new Uri(Path.GetFullPath(Payload.AddinPath)).AbsoluteUri;

        using (var server = scratch.Root.OpenSubKey(AddinRegistrar.ClsidPath + @"\InprocServer32")!)
        {
            Assert.Equal("mscoree.dll", server.GetValue(""));
            Assert.Equal("Both", server.GetValue("ThreadingModel"));
            Assert.Equal(AddInIdentity.ClassName, server.GetValue("Class"));
            Assert.Equal(assembly.FullName, server.GetValue("Assembly"));
            Assert.Equal("v4.0.30319", server.GetValue("RuntimeVersion"));
            Assert.Equal(codeBase, server.GetValue("CodeBase"));
            using var version = server.OpenSubKey(assembly.Version)!;
            Assert.Equal(codeBase, version.GetValue("CodeBase"));
        }
        using (var progId = scratch.Root.OpenSubKey(AddinRegistrar.ClsidPath + @"\ProgId")!) Assert.Equal(AddInIdentity.ProgId, progId.GetValue(""));
        Assert.NotNull(scratch.Root.OpenSubKey(AddinRegistrar.ClsidPath + @"\Implemented Categories\" + AddinRegistrar.ManagedCategory));
        using (var clsid = scratch.Root.OpenSubKey(AddinRegistrar.ProgIdPath + @"\CLSID")!) Assert.Equal(AddInIdentity.ClsidKey, clsid.GetValue(""));
        using (var addIns = scratch.Root.OpenSubKey(AddinRegistrar.AddInsPath)!)
        {
            Assert.Equal(1, addIns.GetValue(""));
            Assert.Equal(RegistryValueKind.DWord, addIns.GetValueKind(""));
            Assert.Equal(AddInIdentity.Title, addIns.GetValue("Title"));
        }
        using (var startup = scratch.Root.OpenSubKey(AddinRegistrar.StartupPath)!) Assert.Equal(1, startup.GetValue(""));
    }

    [Fact]
    public void Register_is_idempotent_and_status_reports_it()
    {
        using var scratch = new ScratchRegistry();
        var registrar = new AddinRegistrar(scratch.Root);
        registrar.Register(Payload.AddinPath);
        registrar.Register(Payload.AddinPath);
        var status = registrar.Status();
        Assert.True(status.Registered);
        Assert.Equal(Path.GetFullPath(Payload.AddinPath), status.Path);
    }

    [Fact]
    public void Unregister_removes_exactly_the_registered_keys()
    {
        using var scratch = new ScratchRegistry();
        using (var neighbour = scratch.Root.CreateSubKey(@"Software\SolidWorks\AddIns\{00000000-0000-0000-0000-000000000001}")) neighbour.SetValue("", 1);
        var registrar = new AddinRegistrar(scratch.Root);
        registrar.Register(Payload.AddinPath);
        Assert.Equal(4, registrar.Unregister().Count);
        var status = registrar.Status();
        Assert.False(status.ClsidKey || status.ProgIdKey || status.AddInsKey || status.StartupKey);
        Assert.NotNull(scratch.Root.OpenSubKey(@"Software\SolidWorks\AddIns\{00000000-0000-0000-0000-000000000001}"));
        Assert.Empty(registrar.Unregister());
    }

    [Fact]
    public void Status_of_a_registration_whose_file_is_gone_is_not_registered()
    {
        using var scratch = new ScratchRegistry();
        string dir = Path.Combine(Path.GetTempPath(), "swagent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, AddInIdentity.AssemblyFileName);
        File.Copy(Payload.AddinPath, copy);
        var registrar = new AddinRegistrar(scratch.Root);
        registrar.Register(copy);
        File.Delete(copy);
        var status = registrar.Status();
        Assert.False(status.FileExists);
        Assert.False(status.Registered);
    }
}
