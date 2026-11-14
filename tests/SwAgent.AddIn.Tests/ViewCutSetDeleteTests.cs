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
        var before = Drw.ReadView(_detail)!.Position;
        Drw.AfterMove = name => { if (name == _detail) Drw.View(_detail).Drawn![9] *= 2; };
        var e = Fails(() => Set(new ViewSetRequest { View = _detail, At = new SheetPoint(320, 100) }));
        Assert.Equal("extents", e.Error.Details!["check"]);
        Assert.Equal(before.X, Drw.ReadView(_detail)!.Position.X, 9);
        Assert.Equal(300, DrawingReader.Placed(Drw, Drw.ReadView(_detail)!).X, 9);
    }

    [Fact]
    public void A_detail_moves_by_its_circles_centre_not_SOLIDWORKS_position()
    {
        // The fake gives a detail's Position as SP4 does: its own origin, away from the circle's centre (5.B live run).
        var raw = Drw.ReadView(_detail)!.Position;
        Assert.True(System.Math.Abs(raw.X - 300) > 10);
        var v = Set(new ViewSetRequest { View = _detail, At = new SheetPoint(300, 60) }).View;
        Assert.Equal(300, v.Position.X, 9);
        Assert.Equal(60, v.Position.Y, 9);
        // The hole the circle is centred on is drawn there, and SOLIDWORKS' Position moved by the same 40 mm.
        var hole = SwAgent.AddIn.Core.ViewGeometry.ToSheet(Drw.Transform(_detail)!, new Point3(20, 20, 10));
        Assert.Equal(300, hole.X, 9);
        Assert.Equal(60, hole.Y, 9);
        var now = Drw.ReadView(_detail)!.Position;
        Assert.Equal(raw.X, now.X, 9);
        Assert.Equal(raw.Y - 40, now.Y, 9);
    }

    [Fact]
    public void A_detail_SOLIDWORKS_moves_off_where_asked_is_moved_again()
    {
        int moves = 0;
        Drw.AfterMove = name =>
        {
            if (name != _detail || moves++ > 0) return;
            // SOLIDWORKS drew it 0.3 mm right of where it was put (as view.detail saw on creation).
            var v = Drw.View(_detail);
            var t = v.Transform!;
            v.Transform = new SwAgent.AddIn.Core.ViewTransform(t.Rotation, t.TxMm + 0.3, t.TyMm, t.TzMm, t.Scale);
            v.Facts.Position = new SheetPoint(v.Facts.Position.X + 0.3, v.Facts.Position.Y);
        };
        var v = Set(new ViewSetRequest { View = _detail, At = new SheetPoint(300, 60) }).View;
        Assert.Equal(300, v.Position.X, 9);
        Assert.Equal(2, moves);
    }

    [Fact]
    public void A_detail_whose_view_SOLIDWORKS_does_not_move_is_put_back()
    {
        var raw = Drw.ReadView(_detail)!.Position;
        Drw.ViewSetIgnored = true;
        var e = Fails(() => Set(new ViewSetRequest { View = _detail, At = new SheetPoint(300, 60) }));
        Assert.Equal("position", e.Error.Details!["check"]);
        Assert.Contains("circle's centre", e.Message);
        Assert.Equal(raw.Y, Drw.ReadView(_detail)!.Position.Y, 9);
    }

    [Fact]
    public void Drawing_read_gives_a_detail_where_it_draws_its_circles_centre()
    {
        var read = Run(new DrawingReadOperation(Services), new DrawingReadRequest());
        var detail = read.Sheets.SelectMany(s => s.Views).Single(v => v.Name == _detail);
        Assert.Equal(300, detail.Position.X, 9);
        Assert.Equal(100, detail.Position.Y, 9);
        Assert.NotEqual(Drw.ReadView(_detail)!.Position.X, detail.Position.X, 3);
        // The front's own position is its Position.
        Assert.Equal(100, read.Sheets.SelectMany(s => s.Views).Single(v => v.Name == _front.Facts.Name).Position.X, 9);
    }

    [Fact]
    public void A_delete_SOLIDWORKS_refuses_on_the_second_sheet_says_which_views_went_and_which_remain()
    {
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        var top = Drw.AddView("Sheet2", Block, "top", new SheetPoint(100, 150)).Facts.Name;
        Drw.SecondSheetDeleteFails = true;
        var e = Fails(() => Delete(_detail, top));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Equal(new object?[] { _detail }, (System.Collections.Generic.List<object?>)e.Error.Details["deleted"]!);
        Assert.Equal(new object?[] { top }, (System.Collections.Generic.List<object?>)e.Error.Details["remaining"]!);
        Assert.DoesNotContain("nothing had been made", e.Message);
        Assert.Contains($"deleted {_detail} but not {top}", e.Message);
        // The deleted detail's circle went from the front with it; the section's stayed with its view.
        Assert.Equal(new[] { "A" }, Drw.CutLabels(_front.Facts.Name));
        Assert.Null(Drw.ReadView(_detail));
        Assert.NotNull(Drw.ReadView(top));
    }

    [Fact]
    public void A_delete_SOLIDWORKS_refuses_before_deleting_anything_says_none_was_deleted()
    {
        Drw.DeleteFails = true;
        var e = Fails(() => Delete(_section, _detail));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("none was deleted", e.Message);
        Assert.False(e.Error.Details!.ContainsKey("rollback_failed"));
        Assert.NotNull(Drw.ReadView(_detail));
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
