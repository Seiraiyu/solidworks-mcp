using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class DocumentUseTests
{
    /// <summary>An assembly using a bracket and a pin, both loaded only for it, and a plate the user also has open in its own window.</summary>
    static (FakeSolidWorks Sw, FakeDocument Asm, FakeDocument Bracket, FakeDocument Pin, FakeDocument Plate) Scene()
    {
        var sw = new FakeSolidWorks();
        var asm = sw.Track(new FakeDocument("stand.SLDASM", @"C:\w\stand.SLDASM", DocumentType.Assembly));
        var bracket = sw.Track(FakeDocument.Part("bracket.SLDPRT", @"C:\wbracket.SLDPRT"));
        var pin = sw.Track(FakeDocument.Part("pin.SLDPRT", @"C:\w\pin.SLDPRT"));
        var plate = sw.Track(FakeDocument.Part("plate.SLDPRT", @"C:\w\plate.SLDPRT"));
        bracket.Document.Visible = false;
        pin.Document.Visible = false;
        asm.Uses.AddRange(new[] { @"C:\wbracket.SLDPRT", @"C:\W\PIN.sldprt", @"C:\w\plate.SLDPRT" });
        return (sw, asm, bracket, pin, plate);
    }

    [Fact]
    public void An_assembly_uses_its_open_components_whatever_the_case_of_their_paths()
    {
        var (sw, asm, bracket, pin, plate) = Scene();
        Assert.Equal(new[] { bracket, pin, plate }, DocumentUse.Used(sw, asm));
        Assert.Equal(new[] { asm }, DocumentUse.UsedBy(sw, pin));
        Assert.Empty(DocumentUse.UsedBy(sw, asm));
    }

    [Fact]
    public void Closing_an_assembly_unloads_the_hidden_components_nothing_else_uses()
    {
        var (sw, asm, bracket, pin, _) = Scene();
        Assert.Equal(new[] { bracket, pin }, DocumentUse.WouldUnload(sw, asm));
    }

    [Fact]
    public void A_component_another_open_assembly_uses_stays_loaded()
    {
        var (sw, asm, bracket, _, _) = Scene();
        var other = sw.Track(new FakeDocument("bench.SLDASM", @"C:\wbench.SLDASM", DocumentType.Assembly));
        other.Uses.Add(@"C:\w\pin.SLDPRT");
        Assert.Equal(new[] { bracket }, DocumentUse.WouldUnload(sw, asm));
    }

    [Fact]
    public void A_never_saved_document_is_used_by_nothing()
    {
        var (sw, _, _, _, _) = Scene();
        var part = sw.Track(FakeDocument.Part("Part9"));
        Assert.Empty(DocumentUse.UsedBy(sw, part));
    }

    [Fact]
    public void A_component_of_an_assembly_the_agent_owns_is_the_agents()
    {
        var (sw, asm, bracket, _, plate) = Scene();
        var owned = new OwnedDocuments();
        Assert.False(DocumentUse.OwnedByAgent(sw, bracket, owned));
        owned.Add(DocumentKeys.Of(asm.Info()));
        Assert.True(DocumentUse.OwnedByAgent(sw, bracket, owned));
        Assert.True(DocumentUse.OwnedByAgent(sw, plate, owned));
        Assert.True(DocumentUse.OwnedByAgent(sw, asm, owned));
    }
}
