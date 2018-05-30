using NLog;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SimpleRemoteConsole.ServiceInterop
{
    public enum ServiceType
    {
        SERVICE_FILE_SYSTEM_DRIVER = 0x2,
        SERVICE_KERNEL_DRIVER = 0x1,
        SERVICE_WIN32_OWN_PROCESS = 0x10,
        SERVICE_WIN32_SHARE_PROCESS = 0x20,
        SERVICE_USER_OWN_PROCESS = 0x50,
        SERVICE_USER_SHARE_PROCESS = 0x60
    }

    public enum StartType
    {
        SERVICE_AUTO_START = 0x2,
        SERVICE_BOOT_START = 0x0,
        SERVICE_DEMAND_START = 0x3,
        SERVICE_DISABLED = 0x4,
        SERVICE_SYSTEM_START = 0x1
    }

    public enum ErrorControl
    {
        SERVICE_ERROR_CRITICAL = 0x3,
        SERVICE_ERROR_IGNORE = 0x0,
        SERVICE_ERROR_NORMAL = 0x1,
        SERVICE_ERROR_SEVERE = 0x2
    }

    public class Service
    {
        private Logger logger;

        private NativeServiceWrapper.ServiceMainFunction svcMain;
        private NativeServiceWrapper.ServiceControlHandler svcCtrlHandler;

        private AutoResetEvent stopEvent = new AutoResetEvent(false);

        private uint serviceCheckPoint;
        private ServiceStatus svcStatus;
        private IntPtr hSvcStatus;

        private string serviceName;
        private string displayName;
        private string binaryPath;
        private string[] serviceArgs;
        private string username = null;
        private string password = null;
        private ServiceType serviceType = ServiceType.SERVICE_WIN32_OWN_PROCESS;
        private StartType startType = StartType.SERVICE_AUTO_START;
        private ErrorControl errorSeverity = ErrorControl.SERVICE_ERROR_NORMAL;
        private ServiceInfo.OnStart startHandler;
        private ServiceInfo.OnStop stopHandler;

        public Service(ServiceInfo serviceInfo)
        {
            logger = LogManager.GetCurrentClassLogger();

            svcMain = InitializeService;
            svcCtrlHandler = SvcControlHandler;

            serviceName = serviceInfo.ServiceName;
            displayName = serviceInfo.DisplayName;
            binaryPath = serviceInfo.BinaryPath;
            serviceArgs = serviceInfo.ServiceArgs;
            username = serviceInfo.Username;
            password = serviceInfo.Password;
            serviceType = serviceInfo.ServiceType;
            startType = serviceInfo.StartType;
            errorSeverity = serviceInfo.ErrorSeverity;
            startHandler = serviceInfo.StartHandler;
            stopHandler = serviceInfo.StopHandler;

            serviceCheckPoint = 1;
            svcStatus = new ServiceStatus();
            svcStatus.dwServiceType = (uint)serviceType;
            svcStatus.dwServiceSpecificExitCode = 0;
            hSvcStatus = IntPtr.Zero;
        }

        public void CreateService(bool start = true)
        {
            var hSCManager = IntPtr.Zero;
            var hService = IntPtr.Zero;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var errorMsg = "Services can only be installed on Windows platforms.";
                logger.Error("Create service failed: " + errorMsg);
                throw new PlatformNotSupportedException();
            }

            try
            {
                hSCManager = NativeServiceWrapper.OpenSCManagerW(null, null, (uint)SCMPermissions.SC_MANAGER_ALL_ACCESS);
                if (hSCManager == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    var errorMsg = "Error when opening SCM: " + error;

                    if (error == 0x5)
                    {
                        errorMsg = "Error when opening SCM: Access Denied. Please try again with administrator privileges.";
                    }

                    logger.Error("Create service failed: " + errorMsg);
                    throw new Win32Exception(error, errorMsg);
                }

                var serviceCmd = new StringBuilder();
                serviceCmd.AppendFormat("\"{0}\"", binaryPath);
                if (serviceArgs != null)
                {
                    foreach (var arg in serviceArgs)
                    {
                        serviceCmd.Append(" ");
                        serviceCmd.Append(arg);
                    }
                }

                hService = NativeServiceWrapper.CreateServiceW(hSCManager, serviceName, displayName, (uint)ServicePermissions.SERVICE_ALL_ACCESS, serviceType, startType, errorSeverity, serviceCmd.ToString(), null, IntPtr.Zero, null, username, password);
                if (hService == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != (int)ServiceErrorCodes.ERROR_SERVICE_EXISTS)
                    {
                        var errorMsg = "Create service failed with error: " + error;
                        logger.Error("Create service failed: " + errorMsg);
                        throw new Win32Exception(error, errorMsg);
                    }

                    hService = NativeServiceWrapper.OpenServiceW(hSCManager, serviceName, (uint)ServicePermissions.SERVICE_START);
                    if (hService == IntPtr.Zero)
                    {
                        error = Marshal.GetLastWin32Error();
                        var errorMsg = "Could not open the existing service. Error: " + error;
                        logger.Error("Create service failed: " + errorMsg);
                        throw new Win32Exception(error, errorMsg);
                    }
                }

                if (start)
                {
                    LaunchService(hSCManager, hService);
                }
            }
            finally
            {
                if (hService != IntPtr.Zero)
                {
                    NativeServiceWrapper.CloseServiceHandle(hService);
                }

                if (hSCManager != IntPtr.Zero)
                {
                    NativeServiceWrapper.CloseServiceHandle(hSCManager);
                }
            }
        }

        /// <summary>
        /// Launches the service and then returns.
        /// </summary>
        public void LaunchService()
        {
            var hSCManager = NativeServiceWrapper.OpenSCManagerW(null, null, (uint)SCMPermissions.SC_MANAGER_ALL_ACCESS);
            if (hSCManager == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                var errorMsg = "Error when opening SCM: " + error;

                if (error == 0x5)
                {
                    errorMsg = "Error when opening SCM: Access Denied. Please try again with administrator privileges.";
                }

                logger.Error("Launch service failed: " + errorMsg);
                throw new Win32Exception(error, errorMsg);
            }

            try
            {
                LaunchService(hSCManager);
            }
            finally
            {
                NativeServiceWrapper.CloseServiceHandle(hSCManager);
            }
        }

        private void LaunchService(IntPtr hSCManager)
        {
            var hService = NativeServiceWrapper.OpenServiceW(hSCManager, serviceName, (uint)ServicePermissions.SERVICE_START);
            if (hService == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                var errorMsg = "Could not open the existing service. Error: " + error;
                logger.Error("Launch service failed: " + errorMsg);
                throw new Win32Exception(error, errorMsg);
            }

            try
            {
                LaunchService(hSCManager, hService);
            }
            finally
            {
                NativeServiceWrapper.CloseServiceHandle(hService);
            }
        }

        private void LaunchService(IntPtr hSCManager, IntPtr hService)
        {
            var svcArgs = serviceArgs ?? new string[0];
            if (!NativeServiceWrapper.StartServiceW(hService, (uint)svcArgs.Length, svcArgs))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != (int)ServiceErrorCodes.ERROR_SERVICE_ALREADY_RUNNING)
                {
                    var errorMsg = "Error when starting service: " + error;
                    logger.Error("Launch service failed: " + errorMsg);
                    throw new Win32Exception(error, errorMsg);
                }
            }
        }

        public void RemoveService()
        {
            var hSCManager = IntPtr.Zero;
            var hService = IntPtr.Zero;

            try
            {
                hSCManager = NativeServiceWrapper.OpenSCManagerW(null, null, (uint)SCMPermissions.SC_MANAGER_ALL_ACCESS);
                if (hSCManager == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    var errorMsg = "Error when opening SCM: " + error;

                    if (error == 0x5)
                    {
                        errorMsg = "Error when opening SCM: Access Denied. Please try again with administrator privileges.";
                    }
                    
                    logger.Error("Remove service failed: " + errorMsg);
                    throw new Win32Exception(error, errorMsg);
                }

                hService = NativeServiceWrapper.OpenServiceW(hSCManager, serviceName, (uint)ServicePermissions.SERVICE_DELETE | (uint)ServicePermissions.SERVICE_STOP);
                if (hService == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    var errorMsg = "Could not open the service for removal. Error: " + error;
                    logger.Error("Remove service failed: " + errorMsg);
                    throw new Win32Exception(error, errorMsg);
                }

                ServiceStatus status = new ServiceStatus();
                if (!NativeServiceWrapper.ControlService(hService, ServiceControls.SERVICE_CONTROL_STOP, ref status))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != (int)ServiceErrorCodes.ERROR_SERVICE_NOT_ACTIVE &&
                        error != (int)ServiceErrorCodes.ERROR_SERVICE_CANNOT_ACCEPT_CTRL &&
                        error != (int)ServiceErrorCodes.ERROR_SHUTDOWN_IN_PROGRESS)
                    {
                        var errorMsg = "Could not stop the service for removal. Error: " + error;
                        logger.Error("Remove service failed: " + errorMsg);
                        throw new Win32Exception(error, errorMsg);
                    }
                }

                if (!NativeServiceWrapper.DeleteService(hService))
                {
                    var error = Marshal.GetLastWin32Error();
                    var errorMsg = "Could not delete the service. Error: " + error;
                    logger.Error("Remove service failed: " + errorMsg);
                    throw new Win32Exception(error, errorMsg);
                }
            }
            finally
            {
                if (hService != IntPtr.Zero)
                {
                    NativeServiceWrapper.CloseServiceHandle(hService);
                }

                if (hSCManager != IntPtr.Zero)
                {
                    NativeServiceWrapper.CloseServiceHandle(hSCManager);
                }
            }
        }

        /// <summary>
        /// Part of the service start workflow where the main function
        /// calls this function to signal that the service is should start.
        /// This function blocks until the service is stopped.
        /// </summary>
        public void StartService()
        {
            ServiceTableEntry[] dispatchTable = new ServiceTableEntry[2];
            dispatchTable[0].serviceName = serviceName;
            dispatchTable[0].serviceMainFunction = Marshal.GetFunctionPointerForDelegate(svcMain);

            if (!NativeServiceWrapper.StartServiceCtrlDispatcherW(dispatchTable))
            {
                var error = Marshal.GetLastWin32Error();
                var errorMsg = "Could not start the service. Error: " + error;
                logger.Error("Start service control dispatcher failed: " + errorMsg);
                throw new Win32Exception(error, errorMsg);
            }
        }

        /// <summary>
        /// Called by SCM as the entry point of the service. Note that args
        /// should come from the program main since SCM will launch us with
        /// the args passed in like a normal command line program.
        /// </summary>
        /// <param name="argc">The number of arguments passed.</param>
        /// <param name="argv">The string array of arguments.</param>
        private void InitializeService(int argc, IntPtr argv)
        {
            logger.Info("Begin service initialization.");

            hSvcStatus = NativeServiceWrapper.RegisterServiceCtrlHandlerExW(serviceName, svcCtrlHandler, IntPtr.Zero);
            if (hSvcStatus == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                var errorMsg = "Could not register service control handler. Error: " + error;
                logger.Error("Initialize service failed: " + errorMsg);
                throw new Win32Exception(error, errorMsg);
            }
            
            ReportSvcStatus(CurrentState.SERVICE_START_PENDING, 0, 3000);

            Thread.Sleep(30000);
            var strArgs = "\"" + string.Join("\", \"", serviceArgs) + "\"";
            logger.Info("Initializing service with arguments: " + strArgs);

            var exitCode = 0;
            try
            {
                startHandler(serviceArgs);
                ReportSvcStatus(CurrentState.SERVICE_RUNNING, 0, 0);
                logger.Info("Service started successfully.");
                stopEvent.WaitOne();
            }
            catch (Exception e)
            {
                exitCode = -1;
                ReportSvcStatus(CurrentState.SERVICE_STOPPED, exitCode, 0);
                logger.Error("Exception thrown from service start handler: " + e.Message);
                return;
            }

            logger.Info("Stopping service.");
            ReportSvcStatus(CurrentState.SERVICE_STOP_PENDING, 0, 3000);

            try
            {
                stopHandler();
            }
            catch (Exception e)
            {
                exitCode = -1;
                logger.Error("Exception thrown from service stop handler: " + e.Message);
            }

            ReportSvcStatus(CurrentState.SERVICE_STOPPED, exitCode, 0);
            logger.Info("Service stopped successfully.");
        }

        private void SvcControlHandler(uint control, uint eventType, IntPtr eventData, IntPtr eventContext)
        {
            switch ((ServiceControls)control)
            {
                case ServiceControls.SERVICE_CONTROL_STOP:
                    stopEvent.Set();
                    return;
                case ServiceControls.SERVICE_CONTROL_INTERROGATE:
                    break;
                default:
                    break;
            }
        }

        private void ReportSvcStatus(CurrentState state, int exitCode, uint waitHint)
        {
            if (svcStatus.dwCurrentState == (uint)CurrentState.SERVICE_STOPPED)
            {
                return;
            }

            serviceCheckPoint = 1;
            svcStatus.dwCurrentState = (uint)state;
            svcStatus.dwWin32ExitCode = exitCode;
            svcStatus.dwWaitHint = waitHint;

            if (state == CurrentState.SERVICE_START_PENDING)
            {
                svcStatus.dwControlsAccepted = 0;
            }
            else
            {
                svcStatus.dwControlsAccepted = (uint)ControlsAccepted.SERVICE_ACCEPT_STOP;
            }

            if (state == CurrentState.SERVICE_RUNNING || state == CurrentState.SERVICE_STOPPED)
            {
                svcStatus.dwCheckPoint = 0;
            }
            else
            {
                svcStatus.dwCheckPoint = serviceCheckPoint++;
            }

            NativeServiceWrapper.SetServiceStatus(hSvcStatus, ref svcStatus);
        }
    }
}
