using System;
using System.Runtime.InteropServices;

namespace SimpleRemoteConsole.ServiceInterop
{
    internal enum SCMPermissions
    {
        SC_MANAGER_ALL_ACCESS = 0xF003F
    }

    internal enum ServicePermissions
    {
        SERVICE_ALL_ACCESS = 0xF01FF,
        SERVICE_START = 0x10,
        SERVICE_STOP = 0x20,
        SERVICE_DELETE = 0x10000
    }

    internal enum ServiceErrorCodes
    {
        ERROR_SERVICE_EXISTS = 1073,
        ERROR_SERVICE_ALREADY_RUNNING = 1056,
        ERROR_SERVICE_NOT_ACTIVE = 1062,
        ERROR_SERVICE_CANNOT_ACCEPT_CTRL = 1061,
        ERROR_SHUTDOWN_IN_PROGRESS = 1115
    }

    internal static class NativeServiceWrapper
    {
        private const string AdvAPI32Lib = "advapi32.dll";

        internal delegate void ServiceControlHandler(uint control, uint eventType, IntPtr eventData, IntPtr eventContext);
        internal delegate void ServiceMainFunction(int argc, IntPtr argv);
        
        #region Service Imports

        [DllImport(AdvAPI32Lib, ExactSpelling = true, SetLastError = true)]
        internal static extern bool CloseServiceHandle(IntPtr handle);
        
        [DllImport(AdvAPI32Lib, ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName, ServiceControlHandler serviceControlHandler, IntPtr context);
        
        [DllImport(AdvAPI32Lib, ExactSpelling = true, SetLastError = true)]
        internal static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus pServiceStatus);
        
        [DllImport(AdvAPI32Lib, ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr OpenSCManagerW(string machineName, string databaseName, uint dwAccess);
        
        [DllImport(AdvAPI32Lib, ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateServiceW(
            IntPtr serviceControlManager,
            string serviceName,
            string displayName,
            uint desiredControlAccess,
            ServiceType serviceType,
            StartType startType,
            ErrorControl errorSeverity,
            string binaryPath,
            string loadOrderGroup,
            IntPtr outUIntTagId,
            string dependencies,
            string serviceUserName,
            string servicePassword);
        
        [DllImport(AdvAPI32Lib, ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr OpenServiceW(IntPtr serviceControlManager, string serviceName, uint desiredControlAccess);
        
        [DllImport(AdvAPI32Lib, ExactSpelling = true, SetLastError = true)]
        internal static extern bool StartServiceW(IntPtr service, uint argc, string[] wargv);

        [DllImport(AdvAPI32Lib, ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool StartServiceCtrlDispatcherW([MarshalAs(UnmanagedType.LPArray)] ServiceTableEntry[] serviceTable);

        [DllImport(AdvAPI32Lib, ExactSpelling = true, SetLastError = true)]
        internal static extern bool ControlService(IntPtr service, ServiceControls dwControl, ref ServiceStatus pServiceStatus);

        [DllImport(AdvAPI32Lib, ExactSpelling = true, SetLastError = true)]
        internal static extern bool DeleteService(IntPtr service);

        #endregion
    }
}
