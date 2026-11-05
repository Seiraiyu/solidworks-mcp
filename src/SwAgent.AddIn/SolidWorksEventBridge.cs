using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// Turns SOLIDWORKS' application and document events into the add-in's events. Subscribed and unsubscribed on the
/// UI thread. Handlers only read the document that raised them, never throw into SOLIDWORKS, and always return 0.
/// </summary>
internal sealed class SolidWorksEventBridge : IDisposable
{
    readonly ISldWorks _sw;
    readonly SldWorks _app;
    readonly DocumentTracker _docs;
    readonly ILog _log;
    readonly Dictionary<IModelDoc2, DocumentHooks> _hooks = new Dictionary<IModelDoc2, DocumentHooks>();

    public SolidWorksEventBridge(ISldWorks sw, DocumentTracker docs, ILog log)
    {
        _sw = sw;
        _app = (SldWorks)sw;
        _docs = docs;
        _log = log;
    }

    public void Start()
    {
        _app.FileOpenPostNotify += OnFileOpenPost;
        _app.FileNewNotify2 += OnFileNew;
        _app.ActiveModelDocChangeNotify += OnActiveChanged;
        if (_sw.GetDocuments() is object[] docs)
        {
            foreach (var o in docs)
            {
                if (o is IModelDoc2 doc) Hook(doc);
            }
        }
        _docs.Refresh();
    }

    public void Dispose()
    {
        _app.FileOpenPostNotify -= OnFileOpenPost;
        _app.FileNewNotify2 -= OnFileNew;
        _app.ActiveModelDocChangeNotify -= OnActiveChanged;
        foreach (var hooks in _hooks.Values) hooks.Detach();
        _hooks.Clear();
    }

    /// <summary>(errors, warnings) from the Warnings array IModelDocExtension.GetWhatsWrong returns.</summary>
    internal static (int Errors, int Warnings) CountWhatsWrong(object? warnings)
    {
        if (!(warnings is Array flags)) return (0, 0);
        int errors = 0, warns = 0;
        foreach (var f in flags)
        {
            if (f is bool isWarning && isWarning) warns++;
            else errors++;
        }
        return (errors, warns);
    }

    int OnFileOpenPost(string fileName) => Guard("file opened", () =>
    {
        if (_sw.GetOpenDocumentByName(fileName) is IModelDoc2 doc)
        {
            Hook(doc);
            _docs.Opened(Event(doc));
        }
    });

    int OnFileNew(object newDoc, int docType, string templateName) => Guard("file new", () =>
    {
        if (newDoc is IModelDoc2 doc)
        {
            Hook(doc);
            _docs.Opened(Event(doc));
        }
    });

    int OnActiveChanged() => Guard("active document changed", () =>
    {
        if (_sw.ActiveDoc is IModelDoc2 doc) _docs.Activated(Event(doc));
    });

    void Hook(IModelDoc2 doc)
    {
        if (_hooks.ContainsKey(doc)) return;
        var hooks = new DocumentHooks(this, doc);
        hooks.Attach();
        _hooks[doc] = hooks;
    }

    int OnSaved(IModelDoc2 doc) => Guard("saved", () => _docs.Saved(Event(doc)));

    int OnModified() => Guard("modified", _docs.Refresh);

    int OnRebuilt(IModelDoc2 doc) => Guard("rebuilt", () =>
    {
        object features, codes, warnings;
        bool any = doc.Extension.GetWhatsWrong(out features, out codes, out warnings);
        var (errors, warns) = any ? CountWhatsWrong(warnings) : (0, 0);
        var e = Event(doc);
        _docs.Rebuilt(new DocumentRebuiltEvent { Title = e.Title, Path = e.Path, Errors = errors, Warnings = warns });
    });

    int OnDestroyed(IModelDoc2 doc, int destroyType) => Guard("closed", () =>
    {
        if (destroyType != (int)swDestroyNotifyType_e.swDestroyNotifyDestroy) return;
        var e = Event(doc);
        if (_hooks.TryGetValue(doc, out var hooks))
        {
            hooks.Detach();
            _hooks.Remove(doc);
        }
        _docs.Closed(e);
    });

    static DocumentEvent Event(IModelDoc2 doc)
    {
        var info = SolidWorksApplication.Describe(doc);
        return new DocumentEvent { Title = info.Title, Path = info.Path, Type = info.Type };
    }

    int Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _log.Error($"events: handling '{what}' failed", e);
        }
        return 0;
    }

    /// <summary>One document's subscriptions; the interop exposes a separate event interface per document type.</summary>
    sealed class DocumentHooks
    {
        readonly SolidWorksEventBridge _bridge;
        readonly IModelDoc2 _doc;
        readonly DPartDocEvents_FileSavePostNotifyEventHandler? _partSaved;
        readonly DPartDocEvents_ModifyNotifyEventHandler? _partModified;
        readonly DPartDocEvents_RegenPostNotifyEventHandler? _partRebuilt;
        readonly DPartDocEvents_DestroyNotify2EventHandler? _partDestroyed;
        readonly DAssemblyDocEvents_FileSavePostNotifyEventHandler? _asmSaved;
        readonly DAssemblyDocEvents_ModifyNotifyEventHandler? _asmModified;
        readonly DAssemblyDocEvents_RegenPostNotifyEventHandler? _asmRebuilt;
        readonly DAssemblyDocEvents_DestroyNotify2EventHandler? _asmDestroyed;
        readonly DDrawingDocEvents_FileSavePostNotifyEventHandler? _drwSaved;
        readonly DDrawingDocEvents_ModifyNotifyEventHandler? _drwModified;
        readonly DDrawingDocEvents_RegenPostNotifyEventHandler? _drwRebuilt;
        readonly DDrawingDocEvents_DestroyNotify2EventHandler? _drwDestroyed;

        public DocumentHooks(SolidWorksEventBridge bridge, IModelDoc2 doc)
        {
            _bridge = bridge;
            _doc = doc;
            _partSaved = (t, f) => bridge.OnSaved(doc);
            _partModified = bridge.OnModified;
            _partRebuilt = () => bridge.OnRebuilt(doc);
            _partDestroyed = t => bridge.OnDestroyed(doc, t);
            _asmSaved = (t, f) => bridge.OnSaved(doc);
            _asmModified = bridge.OnModified;
            _asmRebuilt = () => bridge.OnRebuilt(doc);
            _asmDestroyed = t => bridge.OnDestroyed(doc, t);
            _drwSaved = (t, f) => bridge.OnSaved(doc);
            _drwModified = bridge.OnModified;
            _drwRebuilt = () => bridge.OnRebuilt(doc);
            _drwDestroyed = t => bridge.OnDestroyed(doc, t);
        }

        public void Attach()
        {
            switch (_doc)
            {
                case PartDoc part:
                    part.FileSavePostNotify += _partSaved;
                    part.ModifyNotify += _partModified;
                    part.RegenPostNotify += _partRebuilt;
                    part.DestroyNotify2 += _partDestroyed;
                    break;
                case AssemblyDoc asm:
                    asm.FileSavePostNotify += _asmSaved;
                    asm.ModifyNotify += _asmModified;
                    asm.RegenPostNotify += _asmRebuilt;
                    asm.DestroyNotify2 += _asmDestroyed;
                    break;
                case DrawingDoc drw:
                    drw.FileSavePostNotify += _drwSaved;
                    drw.ModifyNotify += _drwModified;
                    drw.RegenPostNotify += _drwRebuilt;
                    drw.DestroyNotify2 += _drwDestroyed;
                    break;
            }
        }

        public void Detach()
        {
            try
            {
                switch (_doc)
                {
                    case PartDoc part:
                        part.FileSavePostNotify -= _partSaved;
                        part.ModifyNotify -= _partModified;
                        part.RegenPostNotify -= _partRebuilt;
                        part.DestroyNotify2 -= _partDestroyed;
                        break;
                    case AssemblyDoc asm:
                        asm.FileSavePostNotify -= _asmSaved;
                        asm.ModifyNotify -= _asmModified;
                        asm.RegenPostNotify -= _asmRebuilt;
                        asm.DestroyNotify2 -= _asmDestroyed;
                        break;
                    case DrawingDoc drw:
                        drw.FileSavePostNotify -= _drwSaved;
                        drw.ModifyNotify -= _drwModified;
                        drw.RegenPostNotify -= _drwRebuilt;
                        drw.DestroyNotify2 -= _drwDestroyed;
                        break;
                }
            }
            catch (Exception e)
            {
                _bridge._log.Warn($"events: unsubscribing a document failed: {e.Message}");
            }
        }
    }
}
