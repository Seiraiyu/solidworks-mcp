using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class DocumentLocatorTests
{
    readonly FakeSolidWorks _sw = new FakeSolidWorks();
    readonly FakeDocument _bracket = FakeDocument.Part("bracket.SLDPRT", @"C:\parts\bracket.SLDPRT");
    readonly FakeDocument _new = FakeDocument.Part("Part1");

    public DocumentLocatorTests()
    {
        _sw.Docs.Add(_bracket);
        _sw.Docs.Add(_new);
        _sw.CurrentDoc = _new;
    }

    [Fact]
    public void No_document_named_means_the_active_one()
    {
        Assert.Same(_new, DocumentLocator.Find(_sw, null));
        Assert.Same(_new, DocumentLocator.Find(_sw, " "));
    }

    [Theory]
    [InlineData(@"C:\parts\bracket.SLDPRT")]
    [InlineData(@"c:\PARTS\sub\..\bracket.sldprt")]
    [InlineData("bracket.SLDPRT")]
    [InlineData("bracket")]
    [InlineData("BRACKET")]
    public void A_document_is_found_by_path_title_or_title_without_extension(string name)
    {
        Assert.Same(_bracket, DocumentLocator.Find(_sw, name));
    }

    [Fact]
    public void A_full_title_tells_a_drawing_from_its_model_and_a_bare_name_is_ambiguous()
    {
        var drawing = FakeDocument.NewDrawing("bracket.SLDDRW", @"C:\parts\bracket.SLDDRW");
        _sw.Docs.Add(drawing);
        Assert.Same(drawing, DocumentLocator.Find(_sw, "bracket.SLDDRW"));
        Assert.Same(_bracket, DocumentLocator.Find(_sw, "bracket.sldprt"));
        var e = Assert.Throws<SwAgentException>(() => DocumentLocator.Find(_sw, "bracket"));
        Assert.Contains("more than one", e.Message);
    }

    /// <summary>Issue #36: with extensions hidden SOLIDWORKS titles a part e2e-plate and its drawing e2e-plate - Sheet1.</summary>
    (FakeDocument Part, FakeDocument Drawing) PlateAndDrawing(bool extensionsShown = false)
    {
        var part = FakeDocument.Part(extensionsShown ? "e2e-plate.SLDPRT" : "e2e-plate", @"C:\e2e\e2e-plate.SLDPRT");
        var drawing = FakeDocument.NewDrawing(extensionsShown ? "e2e-plate.SLDDRW - Sheet1" : "e2e-plate - Sheet1", @"C:\e2e\e2e-plate.SLDDRW");
        _sw.Docs.Add(part);
        _sw.Docs.Add(drawing);
        return (part, drawing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_name_with_an_extension_names_only_a_document_of_that_type(bool extensionsShown)
    {
        var (part, drawing) = PlateAndDrawing(extensionsShown);
        Assert.Same(drawing, DocumentLocator.Find(_sw, "e2e-plate.SLDDRW"));
        Assert.Same(drawing, DocumentLocator.Find(_sw, "E2E-PLATE.slddrw"));
        Assert.Same(part, DocumentLocator.Find(_sw, "e2e-plate.SLDPRT"));
        var e = Assert.Throws<SwAgentException>(() => DocumentLocator.Find(_sw, "e2e-plate.SLDASM"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("no open document", e.Message);
    }

    [Fact]
    public void A_drawing_is_found_by_its_full_title()
    {
        var (_, drawing) = PlateAndDrawing();
        Assert.Same(drawing, DocumentLocator.Find(_sw, "e2e-plate - Sheet1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_bare_name_shared_by_documents_of_different_types_is_refused_naming_them(bool extensionsShown)
    {
        PlateAndDrawing(extensionsShown);
        var e = Assert.Throws<SwAgentException>(() => DocumentLocator.Find(_sw, "e2e-plate"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(new object?[] { @"C:\e2e\e2e-plate.SLDPRT", @"C:\e2e\e2e-plate.SLDDRW" }, (System.Collections.Generic.List<object?>)e.Error.Details!["candidates"]!);
        Assert.Contains(@"C:\e2e\e2e-plate.SLDPRT", e.Message);
        Assert.Contains(@"C:\e2e\e2e-plate.SLDDRW", e.Message);
    }

    [Fact]
    public void A_part_and_an_assembly_of_one_name_are_told_apart_only_by_extension()
    {
        var stand = new FakeDocument("stand", @"C:\e2e\stand.SLDASM", DocumentType.Assembly);
        var standPart = FakeDocument.Part("stand", @"C:\e2e\stand.SLDPRT");
        _sw.Docs.Add(stand);
        _sw.Docs.Add(standPart);
        Assert.Same(stand, DocumentLocator.Find(_sw, "stand.SLDASM"));
        Assert.Same(standPart, DocumentLocator.Find(_sw, "stand.SLDPRT"));
        Assert.Throws<SwAgentException>(() => DocumentLocator.Find(_sw, "stand"));
    }

    [Fact]
    public void Typed_lookups_never_resolve_a_drawing_name_to_its_model()
    {
        var (part, drawing) = PlateAndDrawing();
        Assert.Same(drawing, DocumentLocator.FindDrawing(_sw, "e2e-plate.SLDDRW", "drawing.read").Doc);
        Assert.Equal(ErrorCode.WrongDocumentType, Assert.Throws<SwAgentException>(() => DocumentLocator.FindModel(_sw, "e2e-plate.SLDDRW", "model.mass")).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Assert.Throws<SwAgentException>(() => DocumentLocator.FindPart(_sw, "e2e-plate.SLDDRW", "model.tree")).Code);
        Assert.Same(part, DocumentLocator.FindPart(_sw, "e2e-plate.SLDPRT", "model.tree"));
        Assert.Throws<SwAgentException>(() => DocumentLocator.FindDrawing(_sw, "e2e-plate", "drawing.read"));
    }

    [Fact]
    public void An_unsaved_drawing_is_found_by_its_name_without_the_sheet()
    {
        var drawing = FakeDocument.NewDrawing("Draw1 - Sheet1");
        _sw.Docs.Add(drawing);
        Assert.Same(drawing, DocumentLocator.Find(_sw, "Draw1"));
        Assert.Same(drawing, DocumentLocator.Find(_sw, "Draw1.SLDDRW"));
        Assert.Throws<SwAgentException>(() => DocumentLocator.Find(_sw, "Draw1.SLDPRT"));
    }

    [Fact]
    public void A_model_for_a_view_named_by_a_bare_name_its_drawing_shares_is_refused()
    {
        var (part, _) = PlateAndDrawing();
        Assert.Throws<SwAgentException>(() => SavedModels.Require(_sw, "e2e-plate", "/model", out _));
        Assert.Equal(@"C:\e2e\e2e-plate.SLDPRT", SavedModels.Require(_sw, "e2e-plate.SLDPRT", "/model", out var open));
        Assert.Same(part, open);
    }

    [Fact]
    public void A_document_not_open_is_invalid_argument_listing_what_is()
    {
        var e = Assert.Throws<SwAgentException>(() => DocumentLocator.Find(_sw, "plate"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("document", e.Error.Details!["field"]);
        Assert.Equal(new object?[] { @"C:\parts\bracket.SLDPRT", "Part1" }, (System.Collections.Generic.List<object?>)e.Error.Details["open"]!);
    }

    [Fact]
    public void Nothing_open_is_invalid_argument()
    {
        var e = Assert.Throws<SwAgentException>(() => DocumentLocator.Find(new FakeSolidWorks(), null));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("document.new", e.Message);
    }

    [Fact]
    public void Two_documents_with_one_title_must_be_named_by_path()
    {
        _sw.Docs.Add(FakeDocument.Part("bracket.SLDPRT", @"D:\other\bracket.SLDPRT"));
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SwAgentException>(() => DocumentLocator.Find(_sw, "bracket")).Code);
        Assert.Same(_bracket, DocumentLocator.Find(_sw, @"C:\parts\bracket.SLDPRT"));
    }

    [Fact]
    public void A_part_operation_on_an_assembly_is_wrong_document_type()
    {
        _sw.Docs.Add(new FakeDocument("Assem1", null, DocumentType.Assembly));
        var e = Assert.Throws<SwAgentException>(() => DocumentLocator.FindPart(_sw, "Assem1", "model.mass"));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Equal("assembly", e.Error.Details!["type"]);
        Assert.Same(_bracket, DocumentLocator.FindPart(_sw, "bracket", "model.mass"));
    }
}
