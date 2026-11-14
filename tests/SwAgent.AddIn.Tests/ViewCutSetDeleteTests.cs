using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>view.set and view.delete on the block's front view (1:1 at (100, 150)), its section A below it at (100, 100) and its detail B at 2:1 at (300, 100).</summary>
public class ViewCutSetDeleteTests : DrawingTestBase
{
    readonly FakeView _front;
    readonly string _section;
    readonly string _detail;

    public ViewCutSetDeleteTests()
    {
        _front = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        var hole = new ModelPlace { Point = new Point3(20, 20, 10) };
        _section = Run(new ViewSectionOperation(Services), new ViewSectionRequest { Parent = _front.Facts.Name, Cut = new SectionCut { Through = hole } }).View.Name;
        _detail = Run(new ViewDetailOperation(Services), new ViewDetailRequest { Parent = _front.Facts.Name, Centre = hole, RadiusMm = 8, Scale = new ScaleRatio(2, 1), At = new SheetPoint(300, 100) }).View.Name;
        Drw.Calls.Clear();
    }

    ViewResult Set(ViewSetRequest request) => Run(new ViewSetOperation(Services), request);

    ViewDeleteResult Delete(params string[] views) => Run(new ViewDeleteOperation(Services), new ViewDeleteRequest { Views = views.ToList() });

    [Fact]
    public void A_section_moves_along_its_parents_centre_line_drawing_what_it_drew()
    {
        var before = Drw.ReadView(_section)!;
        var v = Set(new ViewSetRequest { View = _section, At = new SheetPoint(100, 80) }).View;
        Assert.Equal(80, v.Position.Y, 9);
        Assert.Equal(before.Position.X, v.Position.X, 9);
    }

    [Fact]
    public void A_section_moved_off_its_parents_centre_line_or_rescaled_is_refused()
    {
        Assert.Equal("/at/x", Fails(() => Set(new ViewSetRequest { View = _section, At = new SheetPoint(120, 80) })).Error.Details!["field"]);
        var e = Fails(() => Set(new ViewSetRequest { View = _section, Scale = new ScaleRatio(1, 2) }));
        Assert.Equal("/scale", e.Error.Details!["field"]);
        Assert.Contains("cut from", e.Message);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void Moving_the_parent_drags_its_section_and_keeps_it_aligned()
    {
        Set(new ViewSetRequest { View = _front.Facts.Name, At = new SheetPoint(110, 150) });
        Assert.Equal(110, Drw.ReadView(_section)!.Position.X, 9);
    }

    [Fact]
    public void A_detail_rescaled_draws_what_it_drew_rescaled_about_its_centre()
    {
        var before = Drw.ReadView(_detail)!;
        var drawn = DrawingReader.Extents(Drw, before)!;
        var v = Set(new ViewSetRequest { View = _detail, Scale = new ScaleRatio(3, 1) }).View;
        Assert.Equal(3, v.Scale.Numerator);
        Assert.Equal(SwAgent.AddIn.Core.ViewGeometry.Width(drawn) * 1.5, SwAgent.AddIn.Core.ViewGeometry.Width(v.Extents!), 9);
        Assert.Equal(300, v.Position.X, 9);
    }

    [Fact]
    public void A_detail_that_does_not_redraw_at_its_new_scale_is_put_back()
    {
        Drw.AfterMove = name => { if (name == _detail) Drw.View(_detail).Drawn![9] *= 2; };
        var e = Fails(() => Set(new ViewSetRequest { View = _detail, At = new SheetPoint(320, 100) }));
        Assert.Equal("extents", e.Error.Details!["check"]);
        Assert.Equal(300, Drw.ReadView(_detail)!.Position.X, 9);
    }

    [Fact]
    public void Deleting_a_section_or_detail_takes_its_line_or_circle_from_the_parent()
    {
        var gone = Delete(_section, _detail);
        Assert.Equal(new[] { _section, _detail }, gone.Deleted);
        Assert.Empty(Drw.CutLabels(_front.Facts.Name));
        Assert.Contains("delete section line A", Drw.Calls);
        Assert.Contains("delete detail circle B", Drw.Calls);
    }

    [Fact]
    public void A_line_SOLIDWORKS_deletes_with_its_view_is_not_deleted_again()
    {
        Drw.DeleteTakesCuts = true;
        Delete(_section);
        Assert.DoesNotContain("delete section line A", Drw.Calls);
        Assert.Equal(new[] { "B" }, Drw.CutLabels(_front.Facts.Name));
    }

    [Fact]
    public void A_line_left_behind_is_reported()
    {
        Drw.CutDeleteFails = true;
        var e = Fails(() => Delete(_section));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Contains("section line A of Drawing View1", e.Message);
    }

    [Fact]
    public void The_parent_deleted_with_its_section_and_detail_takes_their_line_and_circle_with_it()
    {
        Delete(_front.Facts.Name, _section, _detail);
        Assert.DoesNotContain(Drw.Calls, c => c.StartsWith("delete section line", System.StringComparison.Ordinal));
        Assert.Empty(Drw.ViewNames("Sheet1"));
    }
}
