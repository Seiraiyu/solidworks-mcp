using System;
using System.Linq;
using System.Runtime.InteropServices;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AddInClassTests
{
    [Fact]
    public void The_COM_identity_matches_the_contracts()
    {
        var type = typeof(SwAgentAddIn);
        Assert.Equal(AddInIdentity.ClassName, type.FullName);
        Assert.Equal(AddInIdentity.AssemblyFileName, type.Assembly.ManifestModule.Name);
        Assert.Equal(new Guid(AddInIdentity.Clsid), type.GUID);
        Assert.Equal(AddInIdentity.ProgId, ((ProgIdAttribute)type.GetCustomAttributes(typeof(ProgIdAttribute), false).Single()).Value);
        Assert.True(((ComVisibleAttribute)type.GetCustomAttributes(typeof(ComVisibleAttribute), false).Single()).Value);
    }

    [Fact(Skip = "until Task 10: dialog.respond and diag.dialog implementations")]
    public void Every_contract_operation_has_exactly_one_implementation()
    {
        var registry = SwAgentAddIn.CreateRegistry(new AddInServices(new FakeSolidWorks(), "test"));
        Assert.Equal(OperationCatalog.All.Select(o => o.Name).OrderBy(n => n), registry.Names.OrderBy(n => n));
    }

    [Fact]
    public void Diagnostics_are_on_only_when_the_value_is_1()
    {
        using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\SwAgentTests\" + Guid.NewGuid().ToString("N"));
        Assert.False(DiagnosticsSetting.Enabled(root));
        using (var key = root.CreateSubKey(@"Software\solidworks-mcp")) key.SetValue("Diagnostics", 0);
        Assert.False(DiagnosticsSetting.Enabled(root));
        using (var key = root.CreateSubKey(@"Software\solidworks-mcp")) key.SetValue("Diagnostics", 1);
        Assert.True(DiagnosticsSetting.Enabled(root));
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root.Name.Substring(@"HKEY_CURRENT_USER\".Length));
    }
}
