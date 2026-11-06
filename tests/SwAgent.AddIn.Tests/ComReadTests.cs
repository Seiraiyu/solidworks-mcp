using System;
using System.Runtime.InteropServices;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>ComRead against a COM object Windows ships in-process (Scripting.Dictionary), standing in for SOLIDWORKS' undeclared ones.</summary>
public class ComReadTests
{
    [Fact]
    public void A_com_object_is_read_member_by_member_through_its_type_info()
    {
        var type = Type.GetTypeFromProgID("Scripting.Dictionary");
        Assert.NotNull(type);
        object dict = Activator.CreateInstance(type!)!;
        try
        {
            type!.InvokeMember("Add", System.Reflection.BindingFlags.InvokeMethod, null, dict, new object[] { "M5", "Ø5.0" });
            string read = ComRead.Describe(dict, 2);
            Assert.StartsWith("<IDictionary>{", read);
            Assert.Contains("Count=1", read);
            Assert.Equal(new[] { "<IDictionary>" }, ComRead.Items(dict));
        }
        finally
        {
            Marshal.ReleaseComObject(dict);
        }
    }

    [Fact]
    public void Arrays_and_plain_values_read_as_text()
    {
        Assert.Equal("[Ø5.0 | 5 | null]", ComRead.Describe(new object?[] { "Ø5.0", 5, null }, 2));
        Assert.Equal(new[] { "143", "[1, 2]" }, ComRead.Items(new object[] { 143, new[] { 1, 2 } }));
    }
}
