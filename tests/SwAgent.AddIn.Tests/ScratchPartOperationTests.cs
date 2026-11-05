using System;
using System.IO;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ScratchPartOperationTests : IDisposable
{
    readonly string _run = Path.Combine(LiveScratch.Root, "unit-" + Guid.NewGuid().ToString("N"));

    static IOperation Op(FakeSolidWorks sw, bool diagnostics = true) =>
        new DiagScratchPartOperation(new AddInServices(sw, "0.1.0-test",
            new AgentRuntime("0.1.0-test", 4242, NullLog.Instance, diagnosticsEnabled: diagnostics)));

    public void Dispose()
    {
        if (Directory.Exists(_run)) Directory.Delete(_run, recursive: true);
    }

    [Fact]
    public void Creates_a_dirty_part_at_the_scratch_path()
    {
        var sw = new FakeSolidWorks();
        string path = Path.Combine(_run, "killed.SLDPRT");
        var op = Op(sw);
        var request = new DiagScratchPartRequest { Path = path };
        var result = (DocumentInfo)op.Run(request, new TestContext());
        op.Verify(request, result, new TestContext());
        Assert.Equal(path, sw.ScratchParts[0]);
        Assert.Equal(path, result.Path);
        Assert.True(result.Dirty);
        Assert.True(Directory.Exists(_run));
    }

    [Fact]
    public void Refused_while_diagnostics_are_off()
    {
        var sw = new FakeSolidWorks();
        var e = Assert.Throws<SwAgentException>(() =>
            Op(sw, diagnostics: false).Run(new DiagScratchPartRequest { Path = Path.Combine(_run, "a.SLDPRT") }, new TestContext()));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("Diagnostics", e.Message);
        Assert.Empty(sw.ScratchParts);
    }

    [Theory]
    [InlineData(@"C:\work\owner.SLDPRT")]
    [InlineData("relative.SLDPRT")]
    public void Refused_outside_the_scratch_directory(string path)
    {
        var sw = new FakeSolidWorks();
        var e = Assert.Throws<SwAgentException>(() => Op(sw).Run(new DiagScratchPartRequest { Path = path }, new TestContext()));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("path", e.Error.Details!["field"]);
        Assert.Empty(sw.ScratchParts);
    }

    [Fact]
    public void Refused_when_the_file_exists()
    {
        Directory.CreateDirectory(_run);
        string path = Path.Combine(_run, "there.SLDPRT");
        File.WriteAllText(path, "");
        var sw = new FakeSolidWorks();
        var e = Assert.Throws<SwAgentException>(() => Op(sw).Run(new DiagScratchPartRequest { Path = path }, new TestContext()));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Empty(sw.ScratchParts);
    }

    [Fact]
    public void A_part_SOLIDWORKS_did_not_leave_dirty_fails_verification()
    {
        var sw = new FakeSolidWorks { ScratchPartDirty = false };
        string path = Path.Combine(_run, "clean.SLDPRT");
        var op = Op(sw);
        var request = new DiagScratchPartRequest { Path = path };
        var result = (DocumentInfo)op.Run(request, new TestContext());
        Assert.Equal(ErrorCode.VerificationFailed,
            Assert.Throws<SwAgentException>(() => op.Verify(request, result, new TestContext())).Code);
    }
}
