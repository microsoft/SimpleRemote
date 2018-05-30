using System.Runtime.InteropServices;

namespace SimpleRemoteConsole.ServiceInterop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public int dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    internal enum CurrentState
    {
        SERVICE_CONTINUE_PENDING = 0x5,
        SERVICE_PAUSE_PENDING = 0x6,
        SERVICE_PAUSED = 0x7,
        SERVICE_RUNNING = 0x4,
        SERVICE_START_PENDING = 0x2,
        SERVICE_STOP_PENDING = 0x3,
        SERVICE_STOPPED = 0x1
    }

    internal enum ControlsAccepted
    {
        SERVICE_ACCEPT_NETBINDCHANGE = 0x10,
        SERVICE_ACCEPT_PARAMCHANGE = 0x8,
        SERVICE_ACCEPT_PAUSE_CONTINUE = 0x2,
        SERVICE_ACCEPT_PRESHUTDOWN = 0x100,
        SERVICE_ACCEPT_SHUTDOWN = 0x4,
        SERVICE_ACCEPT_STOP = 0x1
    }

    internal enum ServiceControls
    {
        SERVICE_CONTROL_STOP = 0x1,
        SERVICE_CONTROL_INTERROGATE = 0x4
    }
}
