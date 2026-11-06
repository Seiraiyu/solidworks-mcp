using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SwAgent.AddIn.Core;

namespace SwAgent.AddIn.Tests;

/// <summary>SOLIDWORKS as api.call reaches it in the unit tests: plain objects with SOLIDWORKS' member names, called through the same late binding.</summary>
sealed class FakeApiApp
{
    readonly FakeSolidWorks _sw;

    public FakeApiApp(FakeSolidWorks sw)
    {
        _sw = sw;
    }

    /// <summary>Every member called that changes or ends something, in order.</summary>
    public List<string> Called { get; } = new List<string>();

    public string RevisionNumber() => "34.4.0";

    public object? ActiveDoc => (_sw.ActiveDoc() as FakeDocument)?.Api;

    public bool GetUserPreferenceToggle(int UserPreferenceValue) => UserPreferenceValue == 69;

    public bool CloseDoc(string Name)
    {
        Called.Add("CloseDoc");
        return true;
    }

    public void ExitApp() => Called.Add("ExitApp");

    public bool RunMacro2(string FilePathName, string ModuleName, string ProcedureName, int Options, out int Error)
    {
        Called.Add("RunMacro2");
        Error = 0;
        return true;
    }

    public bool CloseUserNotification(int Id)
    {
        Called.Add("CloseUserNotification");
        return true;
    }
}

/// <summary>A document's IModelDoc2, as api.call reaches it.</summary>
sealed class FakeApiDoc
{
    readonly FakeDocument _doc;

    public FakeApiDoc(FakeDocument doc)
    {
        _doc = doc;
        Extension = new FakeApiExtension(doc);
    }

    public List<string> Called { get; } = new List<string>();

    public FakeApiExtension Extension { get; }

    public string GetTitle() => _doc.Info().Title;

    /// <summary>A value, not an object: nothing can be called on it.</summary>
    public int Count => 3;

    public object? Nothing() => null;

    public object? FeatureByName(string Name) => _doc.FeatureByName(Name);

    public void Close() => Called.Add("Close");

    public bool Quit()
    {
        Called.Add("Quit");
        return true;
    }

    /// <summary>Closes the document and opens it (or another file) again, as IModelDoc2.ReloadOrReplace does.</summary>
    public int ReloadOrReplace(bool ReadOnly, string ReplaceFileName, bool DiscardChanges)
    {
        Called.Add("ReloadOrReplace");
        return 0;
    }

    public bool ForceRebuild3(bool TopOnly)
    {
        Called.Add("ForceRebuild3");
        return true;
    }

    /// <summary>The 5 mm between the block's top (z 10) and a plane at z 15, with the closest points, in metres.</summary>
    public double ClosestDistance(object Object1, object Object2, out object Point1, out object Point2)
    {
        Called.Add("ClosestDistance");
        Point1 = new[] { 0.0, 0.0, 0.01 };
        Point2 = new[] { 0.0, 0.0, 0.015 };
        return 0.005;
    }
}

/// <summary>The document's IModelDocExtension, as api.call reaches it.</summary>
sealed class FakeApiExtension
{
    readonly FakeDocument _doc;

    public FakeApiExtension(FakeDocument doc)
    {
        _doc = doc;
    }

    /// <summary>What SelectByID2 was given, each argument with its type.</summary>
    public List<object?[]> Selections { get; } = new List<object?[]>();

    /// <summary>The arrays TakesObjects was given.</summary>
    public List<object> ObjectArrays { get; } = new List<object>();

    /// <summary>The block's mass properties in SI units, as SOLIDWORKS' GetMassProperties2 returns them: centre, volume, area, mass, moments.</summary>
    public object GetMassProperties2(int Accuracy, out int Status, bool UseSelected)
    {
        Status = Accuracy == 1 ? 0 : 2;
        double volume = (_doc.MassProperties?.VolumeMm3 ?? 0) / 1e9;
        return new[] { 0.02, 0.01, 0.005, volume, 0.0028, volume * 1000, 0, 0, 0, 0, 0, 0, 0 };
    }

    public bool SelectByID2(string Name, string Type, double X, double Y, double Z, bool Append, int Mark, object Callout, int SelectOption)
    {
        Selections.Add(new object?[] { Name, Type, X, Y, Z, Append, Mark, Callout, SelectOption });
        return true;
    }

    public int Fails() => throw new COMException("SOLIDWORKS said no", unchecked((int)0x80004005));

    /// <summary>Closes the document and opens it again, as IModelDocExtension.ReloadOrReplace does.</summary>
    public int ReloadOrReplace(bool ReadOnly, string ReplaceFileName, bool DiscardChanges, bool ForceReload)
    {
        Reloads++;
        return 0;
    }

    public int Reloads { get; private set; }

    /// <summary>A value returned through a ref parameter (some interop members declare theirs ref, not out).</summary>
    public bool Counted(ref int Count)
    {
        Count = 3;
        return true;
    }

    public double NotANumber() => double.NaN;

    public object Mixed() => new object?[] { 1, "a", true, null, 2.5f, DayOfWeek.Friday };

    public int TakesObjects(object Objects)
    {
        ObjectArrays.Add(Objects);
        return ((Array)Objects).Length;
    }

    public double Sum(object Values)
    {
        double total = 0;
        foreach (var v in (double[])Values) total += v;
        return total;
    }

    public int Words(object Values) => ((string[])Values).Length;
}

/// <summary>Late binding as SolidWorksBinder does it, with the fakes classified and named as SOLIDWORKS' own objects are.</summary>
sealed class FakeBinder : LateBinder
{
    public List<int> WrappedArrays { get; } = new List<int>();

    public override ApiObjectKind Classify(object target) =>
        target is FakeApiApp ? ApiObjectKind.App : target is FakeApiDoc ? ApiObjectKind.Document : ApiObjectKind.Other;

    public override string InterfaceName(object target) => target switch
    {
        FakeApiApp _ => "ISldWorks",
        FakeApiDoc _ => "IModelDoc2",
        FakeApiExtension _ => "IModelDocExtension",
        FakeEntity e => e.Kind == SwAgent.Contracts.ReferenceKind.Face ? "IFace2" : "IFeature",
        _ => base.InterfaceName(target),
    };

    public override object ObjectArray(object[] items)
    {
        WrappedArrays.Add(items.Length);
        return items;
    }
}

sealed partial class FakeDocument
{
    FakeApiDoc? _api;

    /// <summary>The document's IModelDoc2 as api.call reaches it.</summary>
    public FakeApiDoc Api => _api ??= new FakeApiDoc(this);
}
