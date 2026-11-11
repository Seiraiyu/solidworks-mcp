using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ComponentLookupTests
{
    readonly FakeSolidWorks _sw = new FakeSolidWorks();
    readonly FakeDocument _doc;
    readonly ReferenceService _refs = new ReferenceService(new RenameMap());

    public ComponentLookupTests()
    {
        _doc = _sw.Track(FakeDocument.NewAssembly("stand.SLDASM", @"C:\w\stand.SLDASM"));
        _doc.Asm!.AddComponent("bracket-1", @"C:\w\bracket.SLDPRT", fix: true);
        _doc.Asm.AddComponent("sub-1", @"C:\w\sub.SLDASM");
        _doc.Asm.AddComponent("sub-1/pin-1", @"C:\w\pin.SLDPRT", parent: "sub-1");
    }

    object Find(string text) => ComponentLookup.Find(_refs, _doc, _doc.Asm!, text, "/component");

    [Fact]
    public void A_name_finds_its_component_at_any_level_and_in_any_case()
    {
        Assert.Same(_doc.Asm!.Named("bracket-1").Entity, Find("bracket-1"));
        Assert.Same(_doc.Asm.Named("sub-1/pin-1").Entity, Find("SUB-1/PIN-1"));
    }

    [Fact]
    public void A_reference_finds_the_very_component_it_was_issued_for()
    {
        var pin = _doc.Asm!.Named("sub-1/pin-1");
        string reference = _refs.Issue(_doc, pin.Entity, _doc.Describe(pin.Entity))!;
        Assert.StartsWith("r1.component.", reference);
        Assert.Same(pin.Entity, Find(reference));
    }

    [Fact]
    public void A_deleted_components_reference_is_unresolved_not_another_component()
    {
        var bracket = _doc.Asm!.Named("bracket-1");
        string reference = _refs.Issue(_doc, bracket.Entity, _doc.Describe(bracket.Entity))!;
        _doc.Asm.Delete(new object[] { bracket.Entity });
        var e = Assert.Throws<SwAgentException>(() => Find(reference));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("deleted", e.Error.Details!["reason"]);
        Assert.Contains("component bracket-1", (string)e.Error.Details["last_was"]!);
    }

    [Fact]
    public void A_reference_to_something_else_is_refused()
    {
        var plane = _doc.StandardPlane(0)!;
        string reference = _refs.Issue(_doc, plane, _doc.Describe(plane))!;
        var e = Assert.Throws<SwAgentException>(() => Find(reference));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("plane", e.Error.Details!["kind"]);
    }

    [Fact]
    public void An_unknown_name_lists_the_components()
    {
        var e = Assert.Throws<SwAgentException>(() => Find("pin-1"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/component", e.Error.Details!["field"]);
        Assert.Equal(new object?[] { "bracket-1", "sub-1", "sub-1/pin-1" }, (System.Collections.Generic.List<object?>)e.Error.Details["components"]!);
    }

    [Fact]
    public void An_empty_name_is_refused()
    {
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SwAgentException>(() => Find(" ")).Code);
    }

    [Fact]
    public void Assembly_operations_refuse_a_part_and_part_operations_an_assembly()
    {
        _sw.Track(FakeDocument.Part("plate.SLDPRT", @"C:\w\plate.SLDPRT"));
        var e = Assert.Throws<SwAgentException>(() => DocumentLocator.FindAssembly(_sw, "plate.SLDPRT", "assembly.insert"));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Equal("assembly.insert works on assemblies, and plate.SLDPRT is a part", e.Message);
        e = Assert.Throws<SwAgentException>(() => DocumentLocator.FindPart(_sw, "stand", "feature.extrude"));
        Assert.Equal("feature.extrude works on parts, and stand.SLDASM is an assembly", e.Message);
        var (doc, asm) = DocumentLocator.FindAssembly(_sw, "stand", "assembly.insert");
        Assert.Same(_doc, doc);
        Assert.Same(_doc.Asm, asm);
    }
}
