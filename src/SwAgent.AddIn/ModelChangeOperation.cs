using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// An operation that changes a part (design 371, Model changes). It refuses to start while the user is in the middle of
/// something (busy, user_editing), records everything it makes and edits in a <see cref="ModelTransaction"/>, checks the
/// change against the request, and either returns the evidence block or leaves the part as it found it. Run makes and
/// checks the change; Verify re-reads the part's rebuild errors, and a feature with a new error rolls the change back.
/// One request runs at a time on the UI thread, so the transaction waiting for Verify is held in a field.
/// </summary>
public abstract class ModelChangeOperation<TRequest, TResult> : Operation<TRequest, TResult>
    where TRequest : class
    where TResult : class, IModelChangeResult
{
    Pending? _pending;

    protected ModelChangeOperation(AddInServices services)
    {
        Services = services;
    }

    protected AddInServices Services { get; }

    /// <summary>The part's solid bodies, measured before the change, for Check.</summary>
    protected int BodiesBefore { get; private set; }

    /// <summary>The part's volume (mm³), measured before the change, for Check.</summary>
    protected double VolumeBefore { get; private set; }

    /// <summary>The request's document field: the part's title or path, or null for the active document.</summary>
    protected abstract string? DocumentOf(TRequest request);

    /// <summary>Makes the change, recording in <paramref name="tx"/> everything made or edited as it is made. Never leaves a sketch open.</summary>
    protected abstract TResult Change(ISwDocument doc, TRequest request, ModelTransaction tx, IOperationContext context);

    /// <summary>The operation's own exact checks, re-reading SOLIDWORKS rather than trusting what Change returned; throws when one fails.</summary>
    protected abstract void Check(ISwDocument doc, TRequest request, TResult result);

    protected sealed override TResult Run(TRequest request, IOperationContext context)
    {
        _pending = null;
        var doc = DocumentLocator.FindPart(Services.SolidWorks, DocumentOf(request), Name);
        ModelChangeGuard.Require(Services.SolidWorks, doc, Name);
        int bodiesBefore = doc.SolidBodyCount();
        double volumeBefore = Volume(doc, bodiesBefore);
        BodiesBefore = bodiesBefore;
        VolumeBefore = volumeBefore;
        var errorsBefore = new HashSet<string>(doc.WhatsWrong().Where(i => !i.Warning).Select(i => i.Feature), StringComparer.Ordinal);
        var tx = new ModelTransaction();
        TResult result;
        try
        {
            result = Change(doc, request, tx, context);
        }
        catch (Exception e)
        {
            throw tx.Abort(e, verifying: false);
        }
        try
        {
            Check(doc, request, result);
            result.Change = Evidence(doc, tx, bodiesBefore, volumeBefore);
        }
        catch (Exception e)
        {
            throw tx.Abort(e, verifying: true);
        }
        _pending = new Pending(doc, tx, errorsBefore);
        return result;
    }

    /// <summary>No feature has a rebuild error it did not have before the change.</summary>
    protected sealed override void Verify(TRequest request, TResult result, IOperationContext context)
    {
        var pending = _pending ?? throw new InvalidOperationException($"{Name} was verified without being run");
        _pending = null;
        try
        {
            var added = pending.Doc.WhatsWrong().Where(i => !i.Warning && !pending.ErrorsBefore.Contains(i.Feature)).ToList();
            if (added.Count > 0)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    $"the change left rebuild errors: {string.Join(", ", added.Select(i => $"{i.Feature} ({i.Code})"))}",
                    new JsonObject { ["errors"] = added.Select(i => (object?)$"{i.Feature}: {i.Code}").ToList() });
            }
        }
        catch (Exception e)
        {
            throw pending.Tx.Abort(e, verifying: true);
        }
    }

    /// <summary>The part's solid volume now, mm³: 0 with no solid body.</summary>
    protected static double Volume(ISwDocument doc, int bodies) => bodies == 0 ? 0 : doc.Mass()?.VolumeMm3 ?? 0;

    static ModelChange Evidence(ISwDocument doc, ModelTransaction tx, int bodiesBefore, double volumeBefore)
    {
        var problems = doc.WhatsWrong();
        int bodiesAfter = doc.SolidBodyCount();
        return new ModelChange
        {
            Document = doc.Info(),
            Created = tx.Created.ToList(),
            Changed = tx.Changed.ToList(),
            Rebuild = new RebuildStatus
            {
                Errors = problems.Count(p => !p.Warning),
                Warnings = problems.Count(p => p.Warning),
                Items = problems.ToList(),
            },
            BodiesBefore = bodiesBefore,
            BodiesAfter = bodiesAfter,
            VolumeBeforeMm3 = volumeBefore,
            VolumeAfterMm3 = Volume(doc, bodiesAfter),
            Box = bodiesAfter == 0 ? null : doc.BoundingBox(false),
        };
    }

    sealed class Pending
    {
        public Pending(ISwDocument doc, ModelTransaction tx, HashSet<string> errorsBefore)
        {
            Doc = doc;
            Tx = tx;
            ErrorsBefore = errorsBefore;
        }

        public ISwDocument Doc { get; }
        public ModelTransaction Tx { get; }
        public HashSet<string> ErrorsBefore { get; }
    }
}

/// <summary>
/// The precondition of every model change (design 371, Model changes): the user is not editing a sketch in the part
/// (IModelDoc2.GetActiveSketch2) and has no command open (ISldWorks.GetRunningCommandInfo names its PropertyManager).
/// Both are plain reads. Otherwise busy with user_editing, before anything is changed; the user's sketch or command is
/// never closed.
/// </summary>
public static class ModelChangeGuard
{
    public static void Require(ISolidWorksApplication sw, ISwDocument doc, string operation)
    {
        string? sketch = doc.EditingSketch();
        string? command = sw.OpenCommand();
        if (sketch == null && command == null) return;
        var details = new JsonObject { ["reason"] = "user_editing", ["detected"] = "before", ["started"] = false };
        if (sketch != null) details["sketch"] = sketch;
        if (command != null) details["command"] = command;
        string what = sketch != null
            ? $"the user is editing the sketch {sketch} in {doc.Info().Title}"
            : $"the user has '{command}' open in SOLIDWORKS";
        throw new SwAgentException(ErrorCode.Busy,
            $"{operation} was not started: {what}. That is the user's to finish or cancel; wait for it (or ask them), then try again. Nothing was changed",
            details);
    }
}
