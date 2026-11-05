using System;
using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class OperationRegistryTests
{
    [Fact]
    public void Finds_each_declared_operation()
    {
        var registry = TestOps.Registry();
        Assert.True(registry.TryGet("test.echo", out var echo));
        Assert.Equal(typeof(EchoRequest), echo!.RequestType);
        Assert.False(registry.TryGet("test.nope", out _));
        Assert.Equal(new[] { "test.echo", "test.other" }, registry.Names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Refuses_a_declared_operation_without_implementation()
    {
        var e = Assert.Throws<RegistryException>(() => OperationRegistry.Create(TestOps.Contracts, new IOperation[] { new EchoOperation() }));
        Assert.Contains("'test.other' has no implementation", e.Message);
    }

    [Fact]
    public void Refuses_two_implementations_of_one_operation()
    {
        var e = Assert.Throws<RegistryException>(() =>
            OperationRegistry.Create(TestOps.Contracts, new IOperation[] { new EchoOperation(), new EchoOperation(), new OtherOperation() }));
        Assert.Contains("'test.echo' is implemented twice", e.Message);
    }

    [Fact]
    public void Refuses_an_implementation_no_contract_declares()
    {
        var echoOnly = OperationCatalog.FromTypes(new[] { typeof(EchoRequest) });
        var e = Assert.Throws<RegistryException>(() =>
            OperationRegistry.Create(echoOnly, new IOperation[] { new EchoOperation(), new OtherOperation() }));
        Assert.Contains("which no contract declares", e.Message);
    }

    [Fact]
    public void Discovers_operation_classes_and_reports_ones_that_cannot_be_built()
    {
        var registry = OperationRegistry.Discover(TestOps.Contracts, new[] { typeof(EchoOperation), typeof(OtherOperation), typeof(string) }, t => (IOperation)Activator.CreateInstance(t)!);
        Assert.True(registry.TryGet("test.other", out _));

        var e = Assert.Throws<RegistryException>(() =>
            OperationRegistry.Discover(TestOps.Contracts, new[] { typeof(EchoOperation), typeof(WrongResultOperation) }, t => (IOperation)Activator.CreateInstance(t)!));
        Assert.Contains("WrongResultOperation could not be created", e.Message);
    }

    [Fact]
    public void A_pipe_contract_needs_a_pipe_operation_and_a_UI_contract_a_UI_operation()
    {
        var e = Assert.Throws<RegistryException>(() => OperationRegistry.Create(TestOps.ContractsWithPeek, new IOperation[]
        {
            new EchoOperation(), new OtherOperation(), new WrongThreadPeekOperation(),
        }));
        Assert.Contains(e.Problems, p => p.Contains("'test.peek' is declared to run on the pipe thread"));
    }

    [Fact]
    public void A_pipe_operation_is_registered_and_known_as_one()
    {
        var registry = TestOps.RegistryWithPeek();
        Assert.True(registry.TryGet("test.peek", out var op));
        Assert.True(op is IPipeOperation);
    }

    [Fact]
    public void A_pipe_operation_from_an_assembly_that_references_SOLIDWORKS_is_refused()
    {
        Assert.Contains("SolidWorks.Interop", OperationRegistry.PipeAssemblyProblem(new[] { "mscorlib", "SolidWorks.Interop.sldworks" }) ?? "");
        Assert.Null(OperationRegistry.PipeAssemblyProblem(new[] { "mscorlib", "SwAgent.Contracts" }));
    }
}
