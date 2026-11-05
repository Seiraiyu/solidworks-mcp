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

    [Fact(Skip = "until Task 14: addin.* and diag.sleep implementations")]
    public void Every_contract_operation_has_exactly_one_implementation()
    {
        var registry = SwAgentAddIn.CreateRegistry(new AddInServices(new FakeSolidWorks(), "test"));
        Assert.Equal(OperationCatalog.All.Select(o => o.Name).OrderBy(n => n), registry.Names.OrderBy(n => n));
    }
}
