using System;
using System.IO;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ScratchBlockOperationTests : IDisposable
{
    readonly string _run = Path.Combine(LiveScratch.Root, "unit-" + Guid.NewGuid().ToString("N"));
    readonly FakeSolidWorks _sw = new FakeSolidWorks();

    AddInServices? _services;

    IOperation Op(bool diagnostics = true) =>
        new DiagScratchBlockOperation(_services = new AddInServices(_sw, "0.1.0-test",
            new AgentRuntime("0.1.0-test", 4242, NullLog.Instance, diagnosticsEnabled: diagnostics)));

    public void Dispose()
    {
        if (Directory.Exists(_run)) Directory.Delete(_run, recursive: true);
    }

    [Fact]
    public void Creates_a_block_of_the_size_asked_at_the_scratch_path()
    {
        string path = Path.Combine(_run, "block.SLDPRT");
        var op = Op();
        var request = new DiagScratchBlockRequest { Path = path, XMm = 40, YMm = 20, ZMm = 10 };
        var result = (DocumentInfo)op.Run(request, new TestContext());
        op.Verify(request, result, new TestContext());
        Assert.Equal((path, 40.0, 20.0, 10.0), _sw.ScratchBlocks[0]);
        Assert.Equal(path, result.Path);
        Assert.True(_services!.Runtime.Owned.Contains(path));
    }

    [Fact]
    public void Refused_while_diagnostics_are_off()
    {
        var e = Assert.Throws<SwAgentException>(() =>
            Op(diagnostics: false).Run(new DiagScratchBlockRequest { Path = Path.Combine(_run, "b.SLDPRT"), XMm = 1, YMm = 1, ZMm = 1 }, new TestContext()));
        Assert.Contains("Diagnostics", e.Message);
        Assert.Empty(_sw.ScratchBlocks);
    }

    [Theory]
    [InlineData(@"C:\work\owner.SLDPRT", 1, "path")]
    [InlineData("scratch", 0, "x_mm")]
    [InlineData("scratch", 1001, "x_mm")]
    public void Refused_outside_the_scratch_directory_or_out_of_size(string where, double x, string field)
    {
        string path = where == "scratch" ? Path.Combine(_run, "b.SLDPRT") : where;
        var e = Assert.Throws<SwAgentException>(() => Op().Run(new DiagScratchBlockRequest { Path = path, XMm = x, YMm = 1, ZMm = 1 }, new TestContext()));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(_sw.ScratchBlocks);
    }

    [Fact]
    public void A_block_of_the_wrong_volume_fails_verification()
    {
        string path = Path.Combine(_run, "b.SLDPRT");
        var op = Op();
        var request = new DiagScratchBlockRequest { Path = path, XMm = 40, YMm = 20, ZMm = 10 };
        var result = (DocumentInfo)op.Run(request, new TestContext());
        ((FakeDocument)_sw.Docs[0]).MassProperties!.VolumeMm3 = 7999;
        Assert.Equal(ErrorCode.VerificationFailed, Assert.Throws<SwAgentException>(() => op.Verify(request, result, new TestContext())).Code);
    }
}
