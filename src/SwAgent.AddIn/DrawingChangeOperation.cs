using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// An operation that changes a drawing (design 611, Drawing changes). As <see cref="ModelChangeOperation{TRequest,TResult}"/>
/// and <see cref="AssemblyChangeOperation{TRequest,TResult}"/> are for parts and assemblies: it refuses to start while the
/// user is in the middle of something (busy, user_editing, now including editing the sheet format), records what it makes
/// and edits in a <see cref="ModelTransaction"/> (sheets and views deleted by selection, newest first; edited values put
/// back), checks the change by re-reading the drawing, and returns the evidence block. Verify refuses any change to a
/// model: a drawing operation never edits, rebuilds or dirties one; a model SOLIDWORKS marked as changed, or that gained
/// a rebuild error, rolls the change back.
/// </summary>
public abstract class DrawingChangeOperation<TRequest, TResult> : Operation<TRequest, TResult>
    where TRequest : class
    where TResult : class, IDrawingChangeResult
{
    const string LeftFor = "the drawing is left with unsaved changes for you to inspect (drawing.read)";

    Pending? _pending;

    protected DrawingChangeOperation(AddInServices services)
    {
        Services = services;
    }

    protected AddInServices Services { get; }

    /// <summary>The request's document field: the drawing's title or path, or null for the active document.</summary>
    protected abstract string? DocumentOf(TRequest request);

    /// <summary>Makes the change, recording in <paramref name="tx"/> everything made or edited as it is made.</summary>
    protected abstract TResult Change(ISwDocument doc, ISwDrawing drawing, TRequest request, ModelTransaction tx, IOperationContext context);

    /// <summary>The operation's own exact checks, re-reading SOLIDWORKS rather than trusting what Change returned; throws when one fails.</summary>
    protected abstract void Check(ISwDocument doc, ISwDrawing drawing, TRequest request, TResult result);

    /// <summary>The sheet <paramref name="text"/> names, by reference or name.</summary>
    protected string Sheet(ISwDocument doc, ISwDrawing drawing, string text, string field) => Services.References.ResolveSheet(doc, drawing, text, field);

    /// <summary>The view <paramref name="text"/> names, by reference or name.</summary>
    protected string View(ISwDocument doc, ISwDrawing drawing, string text, string field) => Services.References.ResolveView(doc, drawing, text, field);

    /// <summary>Records a sheet the change made, with its reference; a rollback deletes it by selection and checks it is gone.</summary>
    protected void MadeSheet(ISwDocument doc, ISwDrawing drawing, ModelTransaction tx, string name) =>
        tx.Made(new CreatedItem { Name = name, Kind = ReferenceKind.Sheet, Reference = Services.References.IssueSheet(doc, name) }, () =>
        {
            if (!drawing.DeleteSheet(name) || drawing.SheetNames().Contains(name, StringComparer.Ordinal))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not delete the sheet {name}");
        });

    /// <summary>Records a view the change made, with its reference; a rollback deletes it by selection and checks it is gone.</summary>
    protected void MadeView(ISwDocument doc, ISwDrawing drawing, ModelTransaction tx, string name)
    {
        var facts = drawing.ReadView(name);
        tx.Made(new CreatedItem { Name = name, Kind = ReferenceKind.View, Reference = facts == null ? null : Services.References.IssueView(doc, facts) }, () =>
        {
            if (!drawing.DeleteViews(new[] { name }) || drawing.ReadView(name) != null)
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not delete the view {name}");
        });
    }

    /// <summary>A view as results report it, read now.</summary>
    protected ViewInfo Describe(ISwDocument doc, ISwDrawing drawing, string view) =>
        DrawingReader.View(Services.References, doc, drawing, drawing.ReadView(view) ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"the view {view} is not on the drawing",
            new JsonObject { ["check"] = "exists", ["view"] = view }));

    /// <summary>A sheet as results report it, with its views, read now.</summary>
    protected SheetInfo DescribeSheet(ISwDocument doc, ISwDrawing drawing, string sheet) =>
        DrawingReader.Sheet(Services.References, doc, drawing, drawing.ReadSheet(sheet) ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"the sheet {sheet} is not in the drawing",
            new JsonObject { ["check"] = "exists", ["sheet"] = sheet }), withViews: true);

    protected sealed override TResult Run(TRequest request, IOperationContext context)
    {
        _pending = null;
        var (doc, drawing) = DocumentLocator.FindDrawing(Services.SolidWorks, DocumentOf(request), Name);
        DrawingChangeGuard.Require(Services.SolidWorks, doc, drawing, Name);
        var models = ModelStates.Take(Services.SolidWorks, doc);
        int sheetsBefore = drawing.SheetNames().Count, viewsBefore = DrawingReader.ViewCount(drawing);
        var tx = new ModelTransaction();
        TResult result;
        try
        {
            result = Change(doc, drawing, request, tx, context);
            ArmedDialogs.Show(Services, Name, context);
        }
        catch (Exception e)
        {
            throw tx.Abort(e, verifying: false, LeftFor);
        }
        try
        {
            Check(doc, drawing, request, result);
            result.Change = Evidence(doc, drawing, tx, sheetsBefore, viewsBefore, models);
        }
        catch (Exception e)
        {
            throw tx.Abort(e, verifying: true, LeftFor);
        }
        _pending = new Pending(doc, tx, models);
        return result;
    }

    /// <summary>No model the drawing shows (or any document open before the change) was marked as changed or gained a rebuild error.</summary>
    protected sealed override void Verify(TRequest request, TResult result, IOperationContext context)
    {
        var pending = _pending ?? throw new InvalidOperationException($"{Name} was verified without being run");
        _pending = null;
        try
        {
            var changed = pending.Models.Changed(Services.SolidWorks, pending.Doc);
            if (changed.Count > 0)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    $"the change touched a model, which a drawing operation never does: {string.Join("; ", changed)}",
                    new JsonObject { ["check"] = "models", ["models"] = changed.Select(c => (object?)c).ToList() });
            }
        }
        catch (Exception e)
        {
            throw pending.Tx.Abort(e, verifying: true, LeftFor);
        }
    }

    static DrawingChange Evidence(ISwDocument doc, ISwDrawing drawing, ModelTransaction tx, int sheetsBefore, int viewsBefore, ModelStates before) => new DrawingChange
    {
        Document = doc.Info(),
        Created = tx.Created.ToList(),
        Changed = tx.Changed.ToList(),
        SheetsBefore = sheetsBefore,
        SheetsAfter = drawing.SheetNames().Count,
        ViewsBefore = viewsBefore,
        ViewsAfter = DrawingReader.ViewCount(drawing),
        Models = before.Shown(ModelStates.Take(before.Sw, doc)),
    };

    sealed class Pending
    {
        public Pending(ISwDocument doc, ModelTransaction tx, ModelStates models)
        {
            Doc = doc;
            Tx = tx;
            Models = models;
        }

        public ISwDocument Doc { get; }
        public ModelTransaction Tx { get; }
        public ModelStates Models { get; }
    }
}

/// <summary>
/// Every open document but the drawing, by full path, with its unsaved-changes flag and rebuild error count, and which of
/// them the drawing shows (design 611, Drawing changes: evidence and Verify).
/// </summary>
public sealed class ModelStates
{
    ModelStates(ISolidWorksApplication sw, Dictionary<string, (bool Dirty, int Errors)> states, HashSet<string> shown)
    {
        Sw = sw;
        States = states;
        ShownPaths = shown;
    }

    public ISolidWorksApplication Sw { get; }

    public Dictionary<string, (bool Dirty, int Errors)> States { get; }

    public HashSet<string> ShownPaths { get; }

    public static ModelStates Take(ISolidWorksApplication sw, ISwDocument drawing)
    {
        var states = new Dictionary<string, (bool, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in sw.Documents())
        {
            var info = d.Info();
            if (info.Path == null || DocumentUse.Same(d, drawing)) continue;
            states[Path.GetFullPath(info.Path)] = (info.Dirty, d.WhatsWrong().Count(i => !i.Warning));
        }
        var shown = new HashSet<string>(drawing.UsedFiles().Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        return new ModelStates(sw, states, shown);
    }

    /// <summary>
    /// What changed in a model since this was taken: one that was open and clean and is now marked as changed, or that
    /// has more rebuild errors; and one loaded by the change that is marked as changed. Each as "path: what".
    /// </summary>
    public List<string> Changed(ISolidWorksApplication sw, ISwDocument drawing)
    {
        var now = Take(sw, drawing);
        var changed = new List<string>();
        foreach (var pair in now.States)
        {
            bool known = States.TryGetValue(pair.Key, out var was);
            if (pair.Value.Dirty && !(known && was.Dirty)) changed.Add($"{pair.Key}: marked as having unsaved changes");
            else if (known && pair.Value.Errors > was.Errors) changed.Add($"{pair.Key}: {pair.Value.Errors - was.Errors} more rebuild errors");
        }
        return changed;
    }

    /// <summary>The evidence block's models: each the drawing shows before or after, as it was and as it is (<paramref name="after"/>).</summary>
    public List<DrawingModelState> Shown(ModelStates after) =>
        ShownPaths.Union(after.ShownPaths, StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            bool known = States.TryGetValue(p, out var was);
            after.States.TryGetValue(p, out var now);
            return new DrawingModelState
            {
                Path = p,
                DirtyBefore = known && was.Dirty,
                DirtyAfter = now.Dirty,
                RebuildErrorsBefore = known ? was.Errors : now.Errors,
                RebuildErrorsAfter = now.Errors,
            };
        }).ToList();
}

/// <summary>
/// The precondition of every drawing change (design 611, Drawing changes): phase 3's (no sketch being edited, here a
/// view's sketch; no command open), and the user is not editing the sheet format. Otherwise busy with user_editing,
/// before anything is changed; nothing of the user's is closed.
/// </summary>
public static class DrawingChangeGuard
{
    public static void Require(ISolidWorksApplication sw, ISwDocument doc, ISwDrawing drawing, string operation)
    {
        ModelChangeGuard.Require(sw, doc, operation);
        if (!drawing.EditingSheetFormat()) return;
        throw new SwAgentException(ErrorCode.Busy,
            $"{operation} was not started: the user is editing the sheet format of {doc.Info().Title}. That is the user's to finish (Edit Sheet); wait for it (or ask them), then try again. Nothing was changed",
            new JsonObject { ["reason"] = "user_editing", ["detected"] = "before", ["started"] = false, ["sheet_format"] = true });
    }
}
