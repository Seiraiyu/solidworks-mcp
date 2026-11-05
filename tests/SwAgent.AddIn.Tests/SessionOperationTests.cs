using System;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

sealed class FakeSolidWorks : ISolidWorksApplication
{
    public string RevisionNumber { get; set; } = "34.4.0";
    public int ProcessId { get; set; } = 4242;
    public DocumentInfo? Active { get; set; }
    public Func<DocumentInfo?>? ActiveOnSecondRead { get; set; }
    public int Count { get; set; }
    int _reads;

    public DocumentInfo? ActiveDocument()
    {
        _reads++;
        return _reads > 1 && ActiveOnSecondRead != null ? ActiveOnSecondRead() : Active;
    }

    public int OpenDocumentCount() => Count;

    public System.Collections.Generic.List<DocumentInfo> Open { get; set; } = new System.Collections.Generic.List<DocumentInfo>();

    public System.Collections.Generic.IReadOnlyList<DocumentInfo> OpenDocuments() => Open;

    /// <summary>The paths CreateScratchPart was asked for, in order.</summary>
    public System.Collections.Generic.List<string> ScratchParts { get; } = new System.Collections.Generic.List<string>();

    /// <summary>Whether the part CreateScratchPart returns has unsaved changes, as SOLIDWORKS should report.</summary>
    public bool ScratchPartDirty { get; set; } = true;

    public DocumentInfo CreateScratchPart(string path)
    {
        ScratchParts.Add(path);
        return new DocumentInfo { Title = System.IO.Path.GetFileName(path), Path = path, Type = DocumentType.Part, Dirty = ScratchPartDirty };
    }

    /// <summary>The blocks CreateScratchBlock was asked for; each becomes a tracked part with one body of that volume.</summary>
    public System.Collections.Generic.List<(string Path, double X, double Y, double Z)> ScratchBlocks { get; } = new System.Collections.Generic.List<(string, double, double, double)>();

    public DocumentInfo CreateScratchBlock(string path, double xMm, double yMm, double zMm)
    {
        ScratchBlocks.Add((path, xMm, yMm, zMm));
        var doc = Track(FakeDocument.Part(System.IO.Path.GetFileName(path), path));
        doc.Bodies = 1;
        doc.MassProperties = new ModelMassResult { VolumeMm3 = xMm * yMm * zMm, DensityKgM3 = 1000, MassG = xMm * yMm * zMm * 1e-3 };
        System.IO.File.WriteAllText(path, "block");
        return doc.Info();
    }

    /// <summary>The documents the document-level operations see; the first is active unless <see cref="CurrentDoc"/> says otherwise.</summary>
    public System.Collections.Generic.List<FakeDocument> Docs { get; } = new System.Collections.Generic.List<FakeDocument>();

    public FakeDocument? CurrentDoc { get; set; }

    public System.Collections.Generic.IReadOnlyList<ISwDocument> Documents() => Docs;

    public ISwDocument? ActiveDoc() => CurrentDoc ?? (Docs.Count > 0 ? Docs[0] : null);

    /// <summary>The title of a command (PropertyManager) the user has open, or null.</summary>
    public string? OpenCommandTitle { get; set; }

    public string? OpenCommand() => OpenCommandTitle;

    /// <summary>What DefaultPartTemplate returns.</summary>
    public string? DefaultTemplate { get; set; }

    /// <summary>The templates NewDocument was given, in order.</summary>
    public System.Collections.Generic.List<string> NewFrom { get; } = new System.Collections.Generic.List<string>();

    /// <summary>When false, NewDocument makes a document without the template's planes (a template that is not a part's).</summary>
    public bool NewHasPlanes { get; set; } = true;

    public string? DefaultPartTemplate() => DefaultTemplate;

    public ISwDocument NewDocument(string template)
    {
        NewFrom.Add(template);
        var doc = NewHasPlanes ? FakeDocument.Part("Part" + (NewFrom.Count + 1)) : new FakeDocument("Part" + (NewFrom.Count + 1));
        Track(doc);
        doc.Document.Dirty = false;
        CurrentDoc = doc;
        return doc;
    }

    /// <summary>The next Open: the document it returns (null for a failure) and SOLIDWORKS' flags.</summary>
    public System.Func<string, OpenOutcome>? OpenWith { get; set; }

    public System.Collections.Generic.List<(string Path, DocumentType Type, bool ReadOnly)> Opened { get; } = new System.Collections.Generic.List<(string, DocumentType, bool)>();

    public OpenOutcome OpenDocument(string path, DocumentType type, bool readOnly)
    {
        Opened.Add((path, type, readOnly));
        var outcome = OpenWith?.Invoke(path)
            ?? new OpenOutcome(new FakeDocument(System.IO.Path.GetFileName(path), path, type) { ReadOnly = readOnly }, 0, 0);
        if (outcome.Document is FakeDocument doc)
        {
            Track(doc);
            CurrentDoc = doc;
        }
        return outcome;
    }

    public System.Collections.Generic.List<string> ClosedTitles { get; } = new System.Collections.Generic.List<string>();

    public void Close(ISwDocument document)
    {
        var doc = (FakeDocument)document;
        ClosedTitles.Add(doc.Document.Title);
        Docs.Remove(doc);
        if (ReferenceEquals(CurrentDoc, doc)) CurrentDoc = null;
    }

    /// <summary>Adds a document the operations can see, wired so Activate makes it the active one.</summary>
    public FakeDocument Track(FakeDocument doc)
    {
        if (!Docs.Contains(doc)) Docs.Add(doc);
        doc.Activated = () => CurrentDoc = doc;
        return doc;
    }
}

sealed class TestContext : IOperationContext
{
    public string Method => "test";
    public DateTime DeadlineUtc => DateTime.UtcNow.AddSeconds(30);
    public ILog Log => NullLog.Instance;
}

public class SolidWorksApplicationTests
{
    [Theory]
    [InlineData((int)swDocumentTypes_e.swDocPART, DocumentType.Part)]
    [InlineData((int)swDocumentTypes_e.swDocASSEMBLY, DocumentType.Assembly)]
    [InlineData((int)swDocumentTypes_e.swDocDRAWING, DocumentType.Drawing)]
    public void Maps_SOLIDWORKS_document_types(int swType, DocumentType expected)
    {
        Assert.Equal(expected, SolidWorksApplication.MapType(swType));
    }

    [Fact]
    public void An_unknown_document_type_is_an_api_error()
    {
        var e = Assert.Throws<SwAgentException>(() => SolidWorksApplication.MapType((int)swDocumentTypes_e.swDocLAYOUT));
        Assert.Equal(ErrorCode.ApiError, e.Code);
    }
}

public class SessionOperationTests
{
    static IOperation Op<T>(FakeSolidWorks sw, Func<int>? queued = null) where T : IOperation
    {
        var services = new AddInServices(sw, "0.1.0-test");
        if (queued != null) services.QueuedRequests = queued;
        return (IOperation)Activator.CreateInstance(typeof(T), services);
    }

    [Fact]
    public void Hello_reports_the_protocol_versions_and_the_process()
    {
        var op = Op<SessionHelloOperation>(new FakeSolidWorks());
        var request = new SessionHelloRequest { ProtocolVersion = Protocol.Version, ClientVersion = "c" };
        var result = (SessionHelloResult)op.Run(request, new TestContext());
        op.Verify(request, result, new TestContext());
        Assert.Equal(Protocol.Version, result.ProtocolVersion);
        Assert.Equal("0.1.0-test", result.AddinVersion);
        Assert.Equal("34.4.0", result.SwRevision);
        Assert.Equal(4242, result.ProcessId);
    }

    [Fact]
    public void Status_reports_the_active_document_and_the_queue()
    {
        var sw = new FakeSolidWorks
        {
            Count = 2,
            Active = new DocumentInfo { Title = "Part1", Path = null, Type = DocumentType.Part, Dirty = true },
        };
        var op = Op<SessionStatusOperation>(sw, () => 3);
        var request = new SessionStatusRequest();
        var result = (SessionStatusResult)op.Run(request, new TestContext());
        op.Verify(request, result, new TestContext());
        Assert.Equal("Part1", result.ActiveDocument!.Title);
        Assert.Null(result.ActiveDocument.Path);
        Assert.True(result.ActiveDocument.Dirty);
        Assert.Equal(2, result.OpenDocumentCount);
        Assert.Equal(3, result.QueuedRequests);
        Assert.Equal(Protocol.Version, result.ProtocolVersion);
        Assert.Equal("0.1.0-test", result.AddinVersion);
    }

    [Fact]
    public void Status_with_no_document_reports_none()
    {
        var op = Op<SessionStatusOperation>(new FakeSolidWorks());
        var request = new SessionStatusRequest();
        var result = (SessionStatusResult)op.Run(request, new TestContext());
        op.Verify(request, result, new TestContext());
        Assert.Null(result.ActiveDocument);
        Assert.Equal(0, result.OpenDocumentCount);
    }

    [Fact]
    public void Status_verification_fails_when_the_active_document_changed()
    {
        var sw = new FakeSolidWorks
        {
            Active = new DocumentInfo { Title = "Part1", Type = DocumentType.Part },
            ActiveOnSecondRead = () => new DocumentInfo { Title = "Assem1", Type = DocumentType.Assembly },
        };
        var op = Op<SessionStatusOperation>(sw);
        var request = new SessionStatusRequest();
        var result = op.Run(request, new TestContext());
        var e = Assert.Throws<SwAgentException>(() => op.Verify(request, result, new TestContext()));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
    }

    sealed class FakePreferences : SwAgent.AddIn.Core.IPreferenceStore
    {
        public readonly System.Collections.Generic.Dictionary<int, bool> Values = new System.Collections.Generic.Dictionary<int, bool>();
        public bool Get(int id) => Values.TryGetValue(id, out var v) ? v : true;
        public void Set(int id, bool value) => Values[id] = value;
    }

    [Fact]
    public void Hello_applies_the_automation_preferences()
    {
        var store = new FakePreferences();
        var guard = new SwAgent.AddIn.Core.PreferenceGuard(store, AutomationPreferences.All,
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N") + ".json"), NullLog.Instance);
        var services = new AddInServices(new FakeSolidWorks(), "0.1.0-test") { Preferences = guard };
        IOperation op = new SessionHelloOperation(services);
        var request = new SessionHelloRequest { ProtocolVersion = Protocol.Version };
        var result = op.Run(request, new TestContext());
        op.Verify(request, result, new TestContext());
        Assert.True(guard.Applied);
        Assert.All(AutomationPreferences.All, p => Assert.False(store.Values[p.Id]));
        guard.Restore("test");
    }

    sealed class StubbornPreferences : SwAgent.AddIn.Core.IPreferenceStore
    {
        public bool Get(int id) => true;
        public void Set(int id, bool value) { } // SOLIDWORKS ignores it
    }

    [Fact]
    public void A_preference_that_will_not_stick_does_not_fail_hello()
    {
        var guard = new SwAgent.AddIn.Core.PreferenceGuard(new StubbornPreferences(), AutomationPreferences.All,
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N") + ".json"), NullLog.Instance);
        var services = new AddInServices(new FakeSolidWorks(), "0.1.0-test") { Preferences = guard };
        IOperation op = new SessionHelloOperation(services);
        var request = new SessionHelloRequest { ProtocolVersion = Protocol.Version };
        var result = op.Run(request, new TestContext());
        op.Verify(request, result, new TestContext());
        Assert.Equal(AutomationPreferences.All.Count, guard.NotTaken.Count);
        guard.Restore("test");
    }
}
