using System;
using System.Runtime.InteropServices;

namespace SimpleRemoteConsole.ServiceInterop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string serviceName;

        internal IntPtr serviceMainFunction;
    }
}
