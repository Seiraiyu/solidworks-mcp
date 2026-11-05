using System;
using System.IO;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>A folder of its own under %TEMP% for one test class, with a part template file in it; deleted afterwards.</summary>
public abstract class DocumentTestBase : IDisposable
{
    protected readonly string Folder = Path.Combine(Path.GetTempPath(), "swmcp-docs-" + Guid.NewGuid().ToString("N"));
    internal readonly FakeSolidWorks Sw = new FakeSolidWorks();
    internal readonly AddInServices Services;

    protected DocumentTestBase()
    {
        Directory.CreateDirectory(Folder);
        Template = Path.Combine(Folder, "Part.PRTDOT");
        File.WriteAllText(Template, "template");
        Sw.DefaultTemplate = Template;
        Services = new AddInServices(Sw, "0.1.0-test");
    }

    protected string Template { get; }

    protected string PathOf(string name) => Path.Combine(Folder, name);

    internal TResult Run<TRequest, TResult>(Operation<TRequest, TResult> op, TRequest request)
        where TRequest : class
        where TResult : class
    {
        IOperation o = op;
        var result = (TResult)o.Run(request, new TestContext());
        o.Verify(request, result, new TestContext());
        return result;
    }

    internal static SwAgentException Fails(Action action) => Assert.Throws<SwAgentException>(action);

    public void Dispose()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
    }
}

public class DocumentNewTests : DocumentTestBase
{
    DocumentNewResult New(DocumentNewRequest request) => Run(new DocumentNewOperation(Services), request);

    [Fact]
    public void Makes_an_unsaved_part_from_the_default_template_and_owns_it()
    {
        var result = New(new DocumentNewRequest());
        Assert.Equal("Part2", result.Document.Title);
        Assert.Null(result.Document.Path);
        Assert.Equal(DocumentType.Part, result.Document.Type);
        Assert.Equal(Template, result.Template);
        Assert.Equal(new[] { Template }, Sw.NewFrom);
        Assert.True(Services.Runtime.Owned.Contains("Part2"));
    }

    [Fact]
    public void With_a_path_saves_it_there_at_once()
    {
        string path = PathOf("block.SLDPRT");
        var result = New(new DocumentNewRequest { Path = path });
        Assert.Equal(path, result.Document.Path);
        Assert.True(File.Exists(path));
        Assert.True(Services.Runtime.Owned.Contains(path));
    }

    [Fact]
    public void A_named_template_is_used()
    {
        string other = PathOf("Metric.PRTDOT");
        File.WriteAllText(other, "t");
        Assert.Equal(other, New(new DocumentNewRequest { Template = other }).Template);
    }

    [Fact]
    public void Only_parts_in_this_version()
    {
        var e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Assembly }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("type", e.Error.Details!["field"]);
        Assert.Empty(Sw.NewFrom);
    }

    [Theory]
    [InlineData("block.SLDASM")]
    [InlineData("relative")]
    public void A_bad_path_is_refused_before_anything_is_made(string name)
    {
        string path = name == "relative" ? "block.SLDPRT" : PathOf(name);
        var e = Fails(() => New(new DocumentNewRequest { Path = path }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("path", e.Error.Details!["field"]);
        Assert.Empty(Sw.NewFrom);
    }

    [Theory]
    [InlineData(@"\parts\block.SLDPRT")]
    [InlineData(@"C:block.SLDPRT")]
    [InlineData(@"block.SLDPRT")]
    public void A_path_relative_to_a_drive_or_folder_is_refused(string path)
    {
        var e = Fails(() => New(new DocumentNewRequest { Path = path }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("full path", e.Message);
        Assert.Empty(Sw.NewFrom);
    }

    [Theory]
    [InlineData(@"C:\parts\a.SLDPRT", true)]
    [InlineData(@"c:/parts/a.SLDPRT", true)]
    [InlineData(@"\\server\share\a.SLDPRT", true)]
    [InlineData(@"\parts\a.SLDPRT", false)]
    [InlineData(@"C:a.SLDPRT", false)]
    [InlineData(@"a.SLDPRT", false)]
    [InlineData(@"\\", false)]
    public void Full_paths_are_drive_or_UNC_paths(string path, bool full)
    {
        Assert.Equal(full, PathRules.IsFull(path));
    }

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        string path = PathOf("there.SLDPRT");
        File.WriteAllText(path, "owner's work");
        Assert.Equal(ErrorCode.InvalidArgument, Fails(() => New(new DocumentNewRequest { Path = path })).Code);
        Assert.Equal("owner's work", File.ReadAllText(path));
    }

    [Fact]
    public void No_default_template_is_operation_failed_saying_where_to_set_it()
    {
        Sw.DefaultTemplate = null;
        var e = Fails(() => New(new DocumentNewRequest()));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("Default Templates", e.Message);
    }

    [Fact]
    public void A_missing_named_template_is_invalid_argument()
    {
        var e = Fails(() => New(new DocumentNewRequest { Template = PathOf("nope.PRTDOT") }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("template", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_save_that_fails_closes_the_new_part()
    {
        var failing = new FailingSaveSolidWorks(Sw);
        var e = Fails(() => Run(new DocumentNewOperation(new AddInServices(failing, "t")), new DocumentNewRequest { Path = PathOf("locked.SLDPRT") }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("swFileLockError", e.Message);
        Assert.Equal(new[] { "Part2" }, Sw.ClosedTitles);
        Assert.Equal(new object?[] { "Part2" }, (System.Collections.Generic.List<object?>)e.Error.Details!["rolled_back"]!);
        Assert.Empty(Sw.Docs);
    }

    [Fact]
    public void A_template_that_is_not_a_parts_fails_verification()
    {
        Sw.NewHasPlanes = false;
        Assert.Equal(ErrorCode.VerificationFailed, Fails(() => New(new DocumentNewRequest())).Code);
    }

    [Fact]
    public void A_save_that_writes_nothing_fails_verification()
    {
        var silent = new SilentSaveSolidWorks(Sw);
        var e = Fails(() => Run(new DocumentNewOperation(new AddInServices(silent, "t")), new DocumentNewRequest { Path = PathOf("ghost.SLDPRT") }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
    }

    /// <summary>A SOLIDWORKS whose new documents fail to save with swFileLockError.</summary>
    sealed class FailingSaveSolidWorks : DelegatingSolidWorks
    {
        public FailingSaveSolidWorks(FakeSolidWorks inner) : base(inner) { }

        public override ISwDocument NewDocument(string template)
        {
            var doc = (FakeDocument)Inner.NewDocument(template);
            doc.FailNextSave = new SaveOutcome(false, 16, 0);
            return doc;
        }
    }

    /// <summary>A SOLIDWORKS whose new documents report a save that wrote no file.</summary>
    sealed class SilentSaveSolidWorks : DelegatingSolidWorks
    {
        public SilentSaveSolidWorks(FakeSolidWorks inner) : base(inner) { }

        public override ISwDocument NewDocument(string template)
        {
            var doc = (FakeDocument)Inner.NewDocument(template);
            doc.SaveWritesNothing = true;
            return doc;
        }
    }
}

public class DocumentOpenTests : DocumentTestBase
{
    DocumentOpenResult Open(DocumentOpenRequest request) => Run(new DocumentOpenOperation(Services), request);

    [Fact]
    public void Opens_a_part_owns_it_and_reports_SOLIDWORKS_warnings_by_name()
    {
        string path = PathOf("plate.SLDPRT");
        File.WriteAllText(path, "part");
        Sw.OpenWith = p => new OpenOutcome(Sw.Track(new FakeDocument("plate.SLDPRT", p)), 0, 8192);
        var result = Open(new DocumentOpenRequest { Path = path });
        Assert.Equal(path, result.Document.Path);
        Assert.False(result.AlreadyOpen);
        Assert.Equal(new[] { "swFileLoadWarning_ModelOutOfDate" }, result.Warnings);
        Assert.Equal((path, DocumentType.Part, false), Sw.Opened[0]);
        Assert.True(Services.Runtime.Owned.Contains(path));
    }

    [Fact]
    public void Read_only_is_passed_on_and_reported()
    {
        string path = PathOf("plate.SLDASM");
        File.WriteAllText(path, "asm");
        var result = Open(new DocumentOpenRequest { Path = path, ReadOnly = true });
        Assert.True(result.ReadOnly);
        Assert.Equal((path, DocumentType.Assembly, true), Sw.Opened[0]);
    }

    [Fact]
    public void An_open_document_is_activated_not_reopened_and_stays_the_users()
    {
        string path = PathOf("owner.SLDPRT");
        File.WriteAllText(path, "part");
        var owners = Sw.Track(new FakeDocument("owner.SLDPRT", path));
        Sw.CurrentDoc = Sw.Track(FakeDocument.Part("Part1"));
        var result = Open(new DocumentOpenRequest { Path = path.ToUpperInvariant() });
        Assert.True(result.AlreadyOpen);
        Assert.Empty(Sw.Opened);
        Assert.Equal(1, owners.Activations);
        Assert.Same(owners, Sw.CurrentDoc);
        Assert.False(Services.Runtime.Owned.Contains(path));
    }

    [Fact]
    public void A_file_that_does_not_exist_is_invalid_argument()
    {
        var e = Fails(() => Open(new DocumentOpenRequest { Path = PathOf("nope.SLDPRT") }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("path", e.Error.Details!["field"]);
        Assert.Empty(Sw.Opened);
    }

    [Fact]
    public void A_file_SOLIDWORKS_will_not_open_is_operation_failed_with_its_errors()
    {
        string path = PathOf("future.SLDPRT");
        File.WriteAllText(path, "part");
        Sw.OpenWith = _ => new OpenOutcome(null, 8192, 0);
        var e = Fails(() => Open(new DocumentOpenRequest { Path = path }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("swFutureVersion", e.Message);
    }

    [Fact]
    public void Codes_name_every_bit_and_unknown_ones_by_value()
    {
        Assert.Equal(new[] { "swGenericSaveError", "swFileLockError" }, SolidWorksCodes.SaveErrors(1 | 16));
        Assert.Equal(new[] { "unknown_1073741824" }, SolidWorksCodes.LoadErrors(1 << 30));
        Assert.Empty(SolidWorksCodes.LoadWarnings(0));
    }
}

/// <summary>Passes everything to a FakeSolidWorks; subclasses change one call.</summary>
abstract class DelegatingSolidWorks : ISolidWorksApplication
{
    protected DelegatingSolidWorks(FakeSolidWorks inner)
    {
        Inner = inner;
    }

    protected FakeSolidWorks Inner { get; }

    public string RevisionNumber => Inner.RevisionNumber;
    public int ProcessId => Inner.ProcessId;
    public DocumentInfo? ActiveDocument() => Inner.ActiveDocument();
    public int OpenDocumentCount() => Inner.OpenDocumentCount();
    public System.Collections.Generic.IReadOnlyList<DocumentInfo> OpenDocuments() => Inner.OpenDocuments();
    public DocumentInfo CreateScratchPart(string path) => Inner.CreateScratchPart(path);
    public System.Collections.Generic.IReadOnlyList<ISwDocument> Documents() => Inner.Documents();
    public ISwDocument? ActiveDoc() => Inner.ActiveDoc();
    public string? DefaultPartTemplate() => Inner.DefaultPartTemplate();
    public virtual ISwDocument NewDocument(string template) => Inner.NewDocument(template);
    public OpenOutcome OpenDocument(string path, DocumentType type, bool readOnly) => Inner.OpenDocument(path, type, readOnly);
    public void Close(ISwDocument document) => Inner.Close(document);
}
