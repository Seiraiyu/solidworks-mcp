using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SwAgent.AddIn.Core.Pipe;

/// <summary>Thrown when the pipe name already exists: another add-in instance, or something squatting on it.</summary>
public sealed class PipeNameInUseException : IOException
{
    public PipeNameInUseException(string name)
        : base($"the pipe name '{name}' is already in use; another add-in instance or another process holds it")
    {
    }
}

internal static class NativePipe
{
    const uint PipeAccessDuplex = 0x00000003;
    const uint FileFlagOverlapped = 0x40000000;
    const uint FileFlagFirstPipeInstance = 0x00080000;
    const uint PipeTypeByte = 0x00000000;
    const uint PipeReadModeByte = 0x00000000;
    const uint PipeWait = 0x00000000;
    const uint PipeRejectRemoteClients = 0x00000008;
    const int ErrorAccessDenied = 5;
    const uint BufferSize = 64 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateNamedPipeW")]
    static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    /// <summary>One server instance of <c>\\.\pipe\&lt;name&gt;</c>, overlapped, local clients only, current user only.</summary>
    public static NamedPipeServerStream Create(string name, bool firstInstance, int maxInstances)
    {
        byte[] descriptor = BuildSecurity().GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf(typeof(SecurityAttributes)),
                SecurityDescriptor = pin.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            uint openMode = PipeAccessDuplex | FileFlagOverlapped | (firstInstance ? FileFlagFirstPipeInstance : 0u);
            uint pipeMode = PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients;
            SafePipeHandle handle = CreateNamedPipe(@"\\.\pipe\" + name, openMode, pipeMode, (uint)maxInstances,
                BufferSize, BufferSize, 0, ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (firstInstance && error == ErrorAccessDenied) throw new PipeNameInUseException(name);
                throw new IOException($"CreateNamedPipe failed for {name}", new Win32Exception(error));
            }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>Full control for the current user; network logons denied; nothing inherited.</summary>
    internal static PipeSecurity BuildSecurity()
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("the current user has no SID");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    public static int ClientProcessId(NamedPipeServerStream stream) =>
        GetNamedPipeClientProcessId(stream.SafePipeHandle, out uint pid) ? (int)pid : -1;
}
