using System;
using System.IO;
using System.Linq;
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
    public void Drawings_are_not_made_in_this_version()
    {
        var e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Drawing }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("type", e.Error.Details!["field"]);
        Assert.Empty(Sw.NewFrom);
    }

    string AssemblyTemplate()
    {
        string template = PathOf("Assembly.ASMDOT");
        File.WriteAllText(template, "template");
        Sw.DefaultAsmTemplate = template;
        return template;
    }

    [Fact]
    public void Makes_an_assembly_from_the_default_assembly_template_and_owns_it()
    {
        string template = AssemblyTemplate();
        string path = PathOf("stand.SLDASM");
        var result = New(new DocumentNewRequest { Type = DocumentType.Assembly, Path = path });
        Assert.Equal(DocumentType.Assembly, result.Document.Type);
        Assert.Equal(template, result.Template);
        Assert.Equal(new[] { template }, Sw.NewFrom);
        Assert.Equal(path, result.Document.Path);
        Assert.True(Services.Runtime.Owned.Contains(path));
    }

    [Fact]
    public void An_assembly_takes_an_assembly_template_and_path_only()
    {
        AssemblyTemplate();
        var e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Assembly, Template = Template }));
        Assert.Equal("template", e.Error.Details!["field"]);
        e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Assembly, Path = PathOf("stand.SLDPRT") }));
        Assert.Equal("path", e.Error.Details!["field"]);
        Assert.Empty(Sw.NewFrom);
    }

    [Fact]
    public void No_default_assembly_template_says_where_to_set_one()
    {
        var e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Assembly }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("default assembly template", e.Message);
    }

    [Fact]
    public void A_command_left_open_on_a_new_assembly_fails_verification()
    {
        AssemblyTemplate();
        Sw.OpenCommandTitle = "Begin Assembly";
        var e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Assembly }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("Begin Assembly", e.Error.Details!["command"]);
        Assert.Contains("insert_component_for_new_assembly", e.Message);
    }

    [Fact]
    public void A_command_open_after_a_new_part_is_not_this_operations_concern()
    {
        Sw.OpenCommandTitle = "Extrude";
        Assert.Equal(DocumentType.Part, New(new DocumentNewRequest()).Document.Type);
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
    public void An_assembly_lists_the_component_files_that_are_not_there()
    {
        string path = PathOf("stand.SLDASM");
        File.WriteAllText(path, "asm");
        string bracket = PathOf("bracket.SLDPRT");
        File.WriteAllText(bracket, "part");
        string pin = PathOf("pin.SLDPRT");
        Sw.OpenWith = p =>
        {
            var asm = new FakeDocument("stand.SLDASM", p, DocumentType.Assembly);
            asm.Uses.AddRange(new[] { bracket, pin });
            return new OpenOutcome(Sw.Track(asm), 0, 0);
        };
        var result = Open(new DocumentOpenRequest { Path = path });
        Assert.Equal(new[] { pin }, result.Missing);
    }

    [Fact]
    public void Nothing_missing_is_an_empty_list()
    {
        string path = PathOf("plate.SLDPRT");
        File.WriteAllText(path, "part");
        Assert.Empty(Open(new DocumentOpenRequest { Path = path }).Missing);
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

public class DocumentSaveTests : DocumentTestBase
{
    DocumentSaveResult Save(DocumentSaveRequest request) => Run(new DocumentSaveOperation(Services), request);

    DocumentSaveResult SaveAs(DocumentSaveAsRequest request) => Run(new DocumentSaveAsOperation(Services), request);

    [Fact]
    public void Save_writes_the_file_where_it_is_and_clears_unsaved_changes()
    {
        string path = PathOf("plate.SLDPRT");
        var doc = Sw.Track(FakeDocument.Part("plate.SLDPRT", path));
        doc.Document.Dirty = true;
        var result = Save(new DocumentSaveRequest { Document = "plate" });
        Assert.Equal(path, result.Path);
        Assert.False(result.Document.Dirty);
        Assert.Equal(4, result.Bytes);
        Assert.Equal(new[] { path }, doc.Saves);
    }

    [Fact]
    public void A_document_never_saved_needs_save_as()
    {
        Sw.Track(FakeDocument.Part("Part1"));
        var e = Fails(() => Save(new DocumentSaveRequest()));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("document.save_as", e.Message);
    }

    [Fact]
    public void A_read_only_document_is_not_saved()
    {
        string path = PathOf("ro.SLDPRT");
        var doc = Sw.Track(new FakeDocument("ro.SLDPRT", path) { ReadOnly = true });
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Save(new DocumentSaveRequest())).Code);
        Assert.Empty(doc.Saves);
    }

    [Fact]
    public void A_save_SOLIDWORKS_refuses_is_operation_failed_with_its_errors()
    {
        var doc = Sw.Track(FakeDocument.Part("plate.SLDPRT", PathOf("plate.SLDPRT")));
        doc.FailNextSave = new SaveOutcome(false, 64, 0);
        var e = Fails(() => Save(new DocumentSaveRequest()));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("swFileSaveWithRebuildError", e.Message);
    }

    [Fact]
    public void A_save_reported_but_not_written_is_verification_failed()
    {
        string path = PathOf("old.SLDPRT");
        File.WriteAllText(path, "old");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        var doc = Sw.Track(FakeDocument.Part("old.SLDPRT", path));
        doc.SaveWritesNothing = true;
        var e = Fails(() => Save(new DocumentSaveRequest()));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Contains("before this call", e.Message);
    }

    [Fact]
    public void Save_as_makes_the_document_that_file_and_its_references_follow()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1"));
        string path = PathOf("bracket.SLDPRT");
        var result = SaveAs(new DocumentSaveAsRequest { Path = path });
        Assert.Equal(path, result.Document.Path);
        Assert.Equal("bracket.SLDPRT", result.Document.Title);
        Assert.True(File.Exists(path));
        Assert.Equal(path, Services.Runtime.Renames.Current("Part1"));
    }

    [Fact]
    public void Save_as_keeps_the_agents_ownership_of_its_part()
    {
        Sw.Track(FakeDocument.Part("Part1"));
        Services.Runtime.Owned.Add("Part1");
        SaveAs(new DocumentSaveAsRequest { Path = PathOf("mine.SLDPRT") });
        Assert.True(Services.Runtime.Owned.Contains(PathOf("mine.SLDPRT")));
    }

    [Fact]
    public void Save_as_refuses_an_existing_file_unless_overwrite()
    {
        Sw.Track(FakeDocument.Part("Part1"));
        string path = PathOf("there.SLDPRT");
        File.WriteAllText(path, "keep");
        Assert.Equal(ErrorCode.InvalidArgument, Fails(() => SaveAs(new DocumentSaveAsRequest { Path = path })).Code);
        Assert.Equal("keep", File.ReadAllText(path));
        SaveAs(new DocumentSaveAsRequest { Path = path, Overwrite = true });
        Assert.Equal("part", File.ReadAllText(path));
    }

    [Fact]
    public void Save_as_needs_the_documents_own_extension()
    {
        Sw.Track(FakeDocument.Part("Part1"));
        var e = Fails(() => SaveAs(new DocumentSaveAsRequest { Path = PathOf("part.STEP") }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains(".SLDPRT", e.Message);
    }

    [Fact]
    public void Save_as_onto_another_open_document_is_refused()
    {
        string path = PathOf("other.SLDPRT");
        File.WriteAllText(path, "other");
        Sw.Track(FakeDocument.Part("other.SLDPRT", path));
        Sw.CurrentDoc = Sw.Track(FakeDocument.Part("Part1"));
        var e = Fails(() => SaveAs(new DocumentSaveAsRequest { Path = path, Overwrite = true }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("open as another document", e.Message);
    }
}

public class DocumentCloseTests : DocumentTestBase
{
    DocumentCloseResult Close(DocumentCloseRequest request) => Run(new DocumentCloseOperation(Services), request);

    [Fact]
    public void Closes_a_document_the_agent_made()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1"));
        Services.Runtime.Owned.Add("Part1");
        var result = Close(new DocumentCloseRequest());
        Assert.Equal("Part1", result.Closed.Title);
        Assert.False(result.Discarded);
        Assert.Equal(new[] { "Part1" }, Sw.ClosedTitles);
        Assert.False(Services.Runtime.Owned.Contains("Part1"));
        _ = doc;
    }

    [Fact]
    public void Refuses_the_users_document_unless_told_the_user_asked()
    {
        Sw.Track(FakeDocument.Part("owner.SLDPRT", PathOf("owner.SLDPRT")));
        var e = Fails(() => Close(new DocumentCloseRequest { Document = "owner" }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("users_document", e.Error.Details!["field"]);
        Assert.Empty(Sw.ClosedTitles);
        Close(new DocumentCloseRequest { Document = "owner", UsersDocument = true });
        Assert.Equal(new[] { "owner.SLDPRT" }, Sw.ClosedTitles);
    }

    [Fact]
    public void Refuses_unsaved_changes_unless_discard()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1"));
        doc.Document.Dirty = true;
        Services.Runtime.Owned.Add("Part1");
        var e = Fails(() => Close(new DocumentCloseRequest()));
        Assert.Equal("discard", e.Error.Details!["field"]);
        Assert.Empty(Sw.ClosedTitles);
        Assert.True(Close(new DocumentCloseRequest { Discard = true }).Discarded);
    }

    [Fact]
    public void A_users_document_with_unsaved_changes_needs_both()
    {
        var doc = Sw.Track(FakeDocument.Part("owner.SLDPRT", PathOf("owner.SLDPRT")));
        doc.Document.Dirty = true;
        Assert.Equal("users_document", Fails(() => Close(new DocumentCloseRequest { Discard = true })).Error.Details!["field"]);
        Assert.Equal("discard", Fails(() => Close(new DocumentCloseRequest { UsersDocument = true })).Error.Details!["field"]);
        Assert.Empty(Sw.ClosedTitles);
        Close(new DocumentCloseRequest { Discard = true, UsersDocument = true });
        Assert.Single(Sw.ClosedTitles);
    }

    [Fact]
    public void A_part_saved_under_a_new_name_is_still_the_agents()
    {
        Sw.Track(FakeDocument.Part("Part1"));
        Services.Runtime.Owned.Add("Part1");
        Run(new DocumentSaveAsOperation(Services), new DocumentSaveAsRequest { Path = PathOf("mine.SLDPRT") });
        Close(new DocumentCloseRequest { Document = "mine" });
        Assert.Equal(new[] { "mine.SLDPRT" }, Sw.ClosedTitles);
    }

    [Fact]
    public void A_file_reopened_after_a_save_as_is_the_agents_and_its_references_name_it()
    {
        string a = PathOf("a.SLDPRT"), b = PathOf("b.SLDPRT");
        Run(new DocumentNewOperation(Services), new DocumentNewRequest { Path = a });
        Run(new DocumentSaveAsOperation(Services), new DocumentSaveAsRequest { Path = b });
        Run(new DocumentOpenOperation(Services), new DocumentOpenRequest { Path = a });
        var reopened = (FakeDocument)Sw.ActiveDoc()!;
        var face = reopened.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane });
        string reference = Services.References.Describe(reopened, face).Reference!;
        Assert.Same(face, Services.References.Resolve(reopened, reference, "face").Entity);
        Run(new DocumentCloseOperation(Services), new DocumentCloseRequest { Document = a });
        Run(new DocumentCloseOperation(Services), new DocumentCloseRequest { Document = b });
        Assert.Equal(new[] { "a.SLDPRT", "b.SLDPRT" }, Sw.ClosedTitles);
    }

    [Fact]
    public void A_document_still_open_after_close_fails_verification()
    {
        var stuck = new StuckSolidWorks(Sw);
        Sw.Track(FakeDocument.Part("Part1"));
        Services.Runtime.Owned.Add("Part1");
        var services = new AddInServices(stuck, "t");
        services.Runtime.Owned.Add("Part1");
        Assert.Equal(ErrorCode.VerificationFailed, Fails(() => Run(new DocumentCloseOperation(services), new DocumentCloseRequest())).Code);
    }

    /// <summary>A SOLIDWORKS that ignores CloseDoc.</summary>
    sealed class StuckSolidWorks : DelegatingSolidWorks
    {
        public StuckSolidWorks(FakeSolidWorks inner) : base(inner) { }

        public override void Close(ISwDocument document)
        {
        }
    }
}

public class RebuildTests : DocumentTestBase
{
    [Fact]
    public void Rebuild_reports_errors_and_warnings_by_feature()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1"));
        doc.Problems.Add(new RebuildItem { Feature = "Fillet1", Code = "swFeatureErrorFilletNoEdge" });
        doc.Problems.Add(new RebuildItem { Feature = "Sketch2", Code = "swSketchErrorUnderDefined", Warning = true });
        var result = Run(new DocumentRebuildOperation(Services), new DocumentRebuildRequest { Force = true });
        Assert.Equal(new[] { true }, doc.Rebuilds);
        Assert.Equal(1, result.Errors);
        Assert.Equal(1, result.Warnings);
        Assert.Equal("Fillet1", result.Items[0].Feature);
    }

    [Fact]
    public void A_failed_rebuild_with_errors_listed_is_reported_not_thrown()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1"));
        doc.RebuildSucceeds = false;
        doc.Problems.Add(new RebuildItem { Feature = "Cut-Extrude1", Code = "swFeatureErrorCutNotIntersectModel" });
        Assert.Equal(1, Run(new DocumentRebuildOperation(Services), new DocumentRebuildRequest()).Errors);
    }

    [Fact]
    public void A_failed_rebuild_with_nothing_listed_is_operation_failed()
    {
        Sw.Track(FakeDocument.Part("Part1")).RebuildSucceeds = false;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Run(new DocumentRebuildOperation(Services), new DocumentRebuildRequest())).Code);
    }

    [Fact]
    public void Rebuild_status_reads_without_rebuilding()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1"));
        var result = Run(new ModelRebuildStatusOperation(Services), new ModelRebuildStatusRequest());
        Assert.Empty(result.Items);
        Assert.Empty(doc.Rebuilds);
    }

    [Fact]
    public void Whats_wrong_arrays_are_read_whatever_their_element_type()
    {
        var typed = SolidWorksDocument.WhatsWrongItems(new object[] { "Fillet1", "Sketch2" }, new[] { 13, 1 }, new[] { false, true }, f => (string?)f);
        var boxed = SolidWorksDocument.WhatsWrongItems(new object[] { "Fillet1", "Sketch2" }, new object[] { 13, 1 }, new object[] { false, true }, f => (string?)f);
        foreach (var items in new[] { typed, boxed })
        {
            Assert.Equal(new[] { "Fillet1", "Sketch2" }, items.Select(i => i.Feature));
            Assert.Equal("swFeatureErrorFilletNoEdge", items[0].Code);
            Assert.True(items[1].Warning);
        }
        Assert.Empty(SolidWorksDocument.WhatsWrongItems(null, null, null, f => null));
    }

    [Fact]
    public void Feature_error_codes_are_named()
    {
        Assert.Equal("swFeatureErrorFilletNoEdge", SolidWorksCodes.FeatureError(13));
        Assert.Equal("unknown_99999", SolidWorksCodes.FeatureError(99999));
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
    public DocumentInfo CreateScratchBlock(string path, double xMm, double yMm, double zMm) => Inner.CreateScratchBlock(path, xMm, yMm, zMm);
    public System.Collections.Generic.IReadOnlyList<ISwDocument> Documents() => Inner.Documents();
    public ISwDocument? ActiveDoc() => Inner.ActiveDoc();
    public string? OpenCommand() => Inner.OpenCommand();
    public string? DefaultPartTemplate() => Inner.DefaultPartTemplate();
    public string? DefaultAssemblyTemplate() => Inner.DefaultAssemblyTemplate();
    public virtual ISwDocument NewDocument(string template) => Inner.NewDocument(template);
    public OpenOutcome OpenDocument(string path, DocumentType type, bool readOnly) => Inner.OpenDocument(path, type, readOnly);
    public virtual void Close(ISwDocument document) => Inner.Close(document);
    public object ApiObject() => Inner.ApiObject();
}
