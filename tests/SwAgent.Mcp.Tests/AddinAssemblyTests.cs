using System;
using System.IO;
using SwAgent.Contracts;
using SwAgent.Mcp.Registration;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class AddinAssemblyTests
{
    [Fact]
    public void Reads_the_addin_identity_without_loading_it()
    {
        var assembly = AddinAssembly.Read(Payload.AddinPath);
        Assert.Equal(AddInIdentity.Clsid, assembly.Clsid);
        Assert.StartsWith("SwAgent.AddIn, Version=", assembly.FullName);
        Assert.EndsWith(", Culture=neutral, PublicKeyToken=null", assembly.FullName);
        Assert.Equal(4, assembly.Version.Split('.').Length);
    }

    [Fact]
    public void Refuses_a_missing_file()
    {
        var e = Assert.Throws<SwAgentException>(() => AddinAssembly.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll")));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
    }

    [Fact]
    public void Refuses_an_assembly_that_is_not_the_addin()
    {
        var e = Assert.Throws<SwAgentException>(() => AddinAssembly.Read(typeof(Cli).Assembly.Location));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains(AddInIdentity.ClassName, e.Message);
    }
}
