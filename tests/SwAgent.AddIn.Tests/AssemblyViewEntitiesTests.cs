using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AssemblyViewEntitiesTests : AssemblyDrawingTestBase
{
    ViewEntitiesResult Entities(string? component = null, string? view = null) =>
        Run(new ViewEntitiesOperation(Services), new ViewEntitiesRequest { View = view ?? AsmFront.Facts.Name, Component = component });

    string RefOf(FakeEntity e) => Services.References.Describe(Asm, e).Reference!;

    [Fact]
    public void An_assembly_view_lists_each_components_entities_with_assembly_references()
    {
        var result = Entities();
        Assert.Equal(Asm.Document.Path, result.Model);
        Assert.Equal(new[] { "block-1", "pin-1" }, result.Entities.Select(e => e.Component));
        var pin = result.Entities.Single(e => e.Component == "pin-1");
        Assert.Equal(RefOf(PinCircle), pin.Reference);
        Assert.Equal(DrawnShape.Circle, pin.Shape);
        Assert.Equal(240, pin.Centre!.X, 9);
        Assert.Equal(150, pin.Centre.Y, 9);
        Assert.Equal("pin-1", pin.Entity!.Component);
        Assert.Same(PinCircle, Services.References.Resolve(Asm, pin.Reference!, "/reference").Entity);
        var block = result.Entities.Single(e => e.Component == "block-1");
        Assert.Equal(220, block.Start!.X, 9);
        Assert.Equal(280, block.End!.X, 9);
    }

    [Fact]
    public void One_component_is_listed_by_name_or_reference()
    {
        Assert.Equal(new[] { "pin-1" }, Entities("pin-1").Entities.Select(e => e.Component));
        string pin = Services.References.Describe(Asm, PinC.Entity).Reference!;
        Assert.Equal(new[] { "pin-1" }, Entities(pin).Entities.Select(e => e.Component));
    }

    [Fact]
    public void A_component_the_view_does_not_show_or_a_component_in_a_part_view_is_refused()
    {
        var hidden = Asm.Asm!.AddComponent("washer-1", PathOf("washer.SLDPRT"));
        var e = Fails(() => Entities("washer-1"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/component", e.Error.Details!["field"]);
        Assert.Contains("block-1, pin-1", e.Message);
        var part = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        var p = Fails(() => Entities("pin-1", part.Facts.Name));
        Assert.Equal("/component", p.Error.Details!["field"]);
        Assert.NotNull(hidden);
    }

    [Fact]
    public void Twin_proxies_are_matched_to_each_components_own_entity()
    {
        // SP4 (5.C live run): the view's proxies share a persistent id the assembly cannot resolve.
        AsmFront.Visible.Clear();
        var pinProxy = Drw.ShowAsProxy(AsmFront, PinCircle);
        var blockProxy = Drw.ShowAsProxy(AsmFront, BlockEdge);
        var result = Entities();
        Assert.Equal(RefOf(PinCircle), result.Entities.Single(e => e.Component == "pin-1").Reference);
        Assert.Equal(RefOf(BlockEdge), result.Entities.Single(e => e.Component == "block-1").Reference);
        Assert.NotSame(pinProxy, PinCircle);
        Assert.NotSame(blockProxy, BlockEdge);
    }

    [Fact]
    public void Proxies_shifted_by_the_view_are_matched_when_two_of_a_component_agree()
    {
        var left = Asm.Asm!.AddEntity("block-1", ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 20, 20), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        AsmFront.Visible.Clear();
        var shift = new Point3(130, 180, 0);
        Drw.ShowAsProxy(AsmFront, BlockEdge, shift);
        Drw.ShowAsProxy(AsmFront, left, shift);
        var block = Entities("block-1").Entities;
        Assert.Equal(new[] { RefOf(BlockEdge), RefOf(left) }, block.Select(e => e.Reference));
    }

    [Fact]
    public void A_note_attaches_to_the_pin_through_the_assembly_view_by_reference_and_lists_it_back()
    {
        string pin = RefOf(PinCircle);
        var note = Run(new AnnotationNoteOperation(Services), new AnnotationNoteRequest
        {
            Text = "PIN", At = new SheetPoint(300, 200), AttachTo = new ViewEntity { View = AsmFront.Facts.Name, Reference = pin },
        }).Annotation;
        var attached = Assert.Single(note.Attached);
        Assert.Equal(pin, attached.Reference);
        Assert.Equal(AsmFront.Facts.Name, note.View);
    }

    [Fact]
    public void A_note_attached_to_a_proxy_that_names_no_component_lists_its_reference_back()
    {
        // SP4 (5.D live run, case 32): the entity a note reads back attached to is the view's proxy, whose component
        // IEntity.GetComponent does not give as the assembly names it. It is matched to the one component the view shows it of.
        Drw.AttachedAsProxies = true;
        Drw.ProxiesReport = BlockEdge.Component;
        string pin = RefOf(PinCircle);
        var note = Run(new AnnotationNoteOperation(Services), new AnnotationNoteRequest
        {
            Text = "PIN", At = new SheetPoint(300, 200), AttachTo = new ViewEntity { View = AsmFront.Facts.Name, Reference = pin },
        }).Annotation;
        Assert.Equal(pin, Assert.Single(note.Attached).Reference);
    }

    [Fact]
    public void A_proxy_the_view_shows_under_two_components_lists_no_reference_for_either()
    {
        // The view decides the component a proxy is of; when it shows the proxy under two, which one is never guessed:
        // no reference is issued, not the pin's and not the block's, and not the one IEntity.GetComponent reports.
        Drw.AttachedAsProxies = true;
        Drw.ProxiesReport = PinC;
        Drw.ProxiesAlsoSameAs = BlockEdge;
        var note = Run(new AnnotationNoteOperation(Services), new AnnotationNoteRequest
        {
            Text = "PIN", At = new SheetPoint(300, 200), AttachTo = new ViewEntity { View = AsmFront.Facts.Name, Reference = RefOf(PinCircle) },
        }).Annotation;
        Assert.Null(Assert.Single(note.Attached).Reference);
    }

    [Fact]
    public void The_component_entity_pair_names_the_pins_circle_through_its_own_part()
    {
        var pinPart = FakeDocument.Part("pin.SLDPRT", PathOf("pin.SLDPRT"));
        var own = pinPart.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(0, 0, 30), Axis = new Vector3(0, 0, 1), RadiusMm = 5, LengthMm = 10 * System.Math.PI });
        PinC.Model = pinPart;
        PinC.Counterparts[own] = PinCircle;
        string inPart = Services.References.Describe(pinPart, own).Reference!;
        var note = Run(new AnnotationNoteOperation(Services), new AnnotationNoteRequest
        {
            Text = "PIN", At = new SheetPoint(300, 200), AttachTo = new ViewEntity { View = AsmFront.Facts.Name, Component = "pin-1", Entity = inPart },
        }).Annotation;
        Assert.Equal(RefOf(PinCircle), Assert.Single(note.Attached).Reference);
    }

    [Fact]
    public void An_entity_of_a_component_the_view_does_not_draw_is_refused_naming_the_view()
    {
        var hidden = Asm.Asm!.AddEntity("pin-1", ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 20, 0), Axis = new Vector3(0, 0, 1), RadiusMm = 5, LengthMm = 10 * System.Math.PI });
        var e = Fails(() => Run(new AnnotationNoteOperation(Services), new AnnotationNoteRequest
        {
            Text = "PIN", At = new SheetPoint(300, 200), AttachTo = new ViewEntity { View = AsmFront.Facts.Name, Reference = RefOf(hidden) },
        }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(AsmFront.Facts.Name, e.Error.Details!["view"]);
    }
}
