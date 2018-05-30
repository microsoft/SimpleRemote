namespace SimpleRemoteConsole.ServiceInterop
{
    public class ServiceInfo
    {
        public delegate void OnStart(string[] args);
        public delegate void OnStop();

        public string ServiceName;
        public string DisplayName = null;
        public string BinaryPath;
        public string[] ServiceArgs;
        public string Username = null;
        public string Password = null;
        public ServiceType ServiceType = ServiceType.SERVICE_WIN32_OWN_PROCESS;
        public StartType StartType = StartType.SERVICE_AUTO_START;
        public ErrorControl ErrorSeverity = ErrorControl.SERVICE_ERROR_NORMAL;
        public OnStart StartHandler;
        public OnStop StopHandler;
    }
}
