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
