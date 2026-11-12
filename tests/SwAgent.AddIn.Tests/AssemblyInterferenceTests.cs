using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AssemblyInterferenceTests : AssemblyTestBase
{
    sealed class LateContext : IOperationContext
    {
        public string Method => "assembly.interference";
        public DateTime DeadlineUtc => DateTime.UtcNow.AddSeconds(-1);
        public ILog Log => NullLog.Instance;
    }

    public AssemblyInterferenceTests()
    {
        Asm.AddComponent("block-1", PathOf("base.SLDPRT"), fix: true);
        Asm.AddComponent("block-2", PathOf("base.SLDPRT"), new Point3(30, 0, 0));
        Asm.AddComponent("block-3", PathOf("base.SLDPRT"), new Point3(0, 20, 0));
        Asm.Clashes.Add(new FakeClash("block-1", "block-2", 2000, new Box3 { Min = new Point3(30, 0, 0), Max = new Point3(40, 20, 10) }));
        Asm.Clashes.Add(new FakeClash("block-1", "block-3", 0, null));
    }

    AssemblyInterferenceResult Find(List<string>? components = null, bool? coincident = null) =>
        Run(new AssemblyInterferenceOperation(Services), new AssemblyInterferenceRequest { Document = "stand.SLDASM", Components = components, CoincidentIsInterference = coincident });

    [Fact]
    public void Overlapping_blocks_report_their_volume_and_box_and_the_session_is_closed()
    {
        var r = Find();
        var one = Assert.Single(r.Interferences);
        Assert.Equal(new[] { "block-1", "block-2" }, one.Components);
        Assert.Equal(2000, one.VolumeMm3);
        Assert.Equal(40, one.Box!.Max.X);
        Assert.False(one.Touching);
        Assert.Equal(2000, r.TotalVolumeMm3);
        Assert.Equal(3, r.Components);
        var session = Assert.Single(Asm.Sessions);
        Assert.Equal(1, session.Dones);
        Assert.False(session.Options.CoincidentIsInterference);
        Assert.Equal(Services.Runtime.InterferenceSessionsOpened, Services.Runtime.InterferenceSessionsClosed);
    }

    [Fact]
    public void Touching_faces_count_only_when_asked_and_come_last()
    {
        var r = Find(coincident: true);
        Assert.Equal(2, r.Interferences.Count);
        Assert.True(r.Interferences[1].Touching);
        Assert.True(Asm.Sessions[0].Options.CoincidentIsInterference);
    }

    [Fact]
    public void Components_keep_the_interferences_that_involve_them()
    {
        Assert.Equal("block-2", Assert.Single(Find(new List<string> { "block-2" }).Interferences).Components[1]);
        Assert.True(Assert.Single(Find(new List<string> { "block-3" }, coincident: true).Interferences).Touching);
    }

    [Fact]
    public void A_listed_subassembly_covers_its_components()
    {
        Asm.AddComponent("sub-1", PathOf("sub.SLDASM"), new Point3(0, 0, 50));
        Asm.AddComponent("sub-1/pin-1", PathOf("pin.SLDPRT"), parent: "sub-1");
        Asm.Clashes.Add(new FakeClash("block-2", "sub-1/pin-1", 12.5, null));
        var r = Find(new List<string> { "sub-1" });
        Assert.Equal("sub-1/pin-1", Assert.Single(r.Interferences).Components[1]);
    }

    [Fact]
    public void An_empty_list_is_invalid()
    {
        var e = Fails(() => Find(new List<string>()));
        Assert.Equal("/components", e.Error.Details!["field"]);
        Assert.Empty(Asm.Sessions);
    }

    [Fact]
    public void Done_runs_when_SOLIDWORKS_fails_mid_read()
    {
        Asm.InterferenceThrows = true;
        var e = Fails(() => Find());
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("session was closed", e.Message);
        Assert.Equal(1, Assert.Single(Asm.Sessions).Dones);
    }

    [Fact]
    public void Done_runs_when_the_deadline_passed_during_the_read()
    {
        IOperation op = new AssemblyInterferenceOperation(Services);
        var result = (AssemblyInterferenceResult)op.Run(new AssemblyInterferenceRequest { Document = "stand.SLDASM" }, new LateContext());
        Assert.Single(result.Interferences);
        Assert.Equal(1, Assert.Single(Asm.Sessions).Dones);
        Assert.Equal(1, Services.Runtime.InterferenceSessionsClosed);
    }

    [Fact]
    public void An_armed_delay_holds_the_open_session_once_and_Done_still_runs()
    {
        Services.Runtime.ArmInterferenceDelay(50);
        var clock = Stopwatch.StartNew();
        Find();
        Assert.True(clock.ElapsedMilliseconds >= 50);
        Assert.Equal(0, Services.Runtime.ArmedInterferenceDelay);
        Assert.Equal(1, Asm.Sessions[0].Dones);
    }

    [Fact]
    public void A_Done_that_fails_is_operation_failed()
    {
        Asm.DoneThrows = true;
        var e = Fails(() => Find());
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("closing", e.Message);
    }

    [Fact]
    public void A_part_is_the_wrong_document_type()
    {
        Sw.Track(FakeDocument.Part("plate.SLDPRT", PathOf("plate.SLDPRT")));
        var e = Fails(() => Run(new AssemblyInterferenceOperation(Services), new AssemblyInterferenceRequest { Document = "plate.SLDPRT" }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
    }
}
