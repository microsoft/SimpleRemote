// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text;
using System.Diagnostics;
using System.Linq;
using NLog;
using System.Reflection;
using System.IO;
using System.Net.Sockets;
using System.Net;
using SimpleDUTRemote.JobSystem;
using System.Threading.Tasks;
using SimpleJsonRpc;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SimpleDUTRemote
{
    /// <summary>
    /// Defines methods that can be called remotely (via RPC).
    /// </summary>
    /// <remarks>The Functions class defines the methods that can be called from a remote
    /// machine. When an RPC request comes in, the RpcServer parses the request, and 
    /// hands it to the Dispatcher object. The Dispatcher determines which method to call 
    /// in this class (by checking the method names), and calls it with the parameters 
    /// provided in the request.
    /// </remarks>
    public class Functions
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();

        // Variables required for state tracking. These are only used by job functions and plugins.
        private ConcurrentDictionary<long, Job> jobs = new ConcurrentDictionary<long, Job>();
        private ConcurrentDictionary<string, object> pluginObjects = new ConcurrentDictionary<string, object>();

        #region Run Functions (Blocking)
        /// <summary>
        /// Run an exe/bat/ps1, and immediately return.
        /// </summary>
        /// <remarks>
        /// Runs an exe, bat, or powershell script on the remote machine, ignoring any output from the process.
        /// <br/>
        /// This function will block until the item finishes, and should not be used for items that will take an 
        /// extended amount of time to run, as waiting on a long-running process may trigger a timeout
        /// exception (depending your client's timeout settings). 
        /// If you need to start a long-running task, consider using StartJob() instead.
        /// </remarks>
        /// <param name="programName">Name of called program (exe/bat/ps1).</param>
        /// <param name="args">Arguments for the called program.</param>
        /// <returns>True on success. Throws exception otherwise.</returns>
        [SimpleRpcMethod]
        public static bool Run(string programName, string args = null)
        {
            var p = SetupProcess(programName, args);
            logger.Info("Starting program...");
            p.Start();
            return true;
        }

        /// <summary>
        /// Start an exe, bat, or ps1 file on the remote machine; block and return result when complete.
        /// </summary>
        /// <remarks>
        /// Runs an exe, bat, or powershell script on the remote machine, capturing output from the process. 
        /// This function will block until the item finishes, and should be used with caution for items that will 
        /// take an extended amount of time to run, as waiting on a long-running process may trigger a timeout
        /// exception on your client (depending on your client's socket settings). 
        /// If you need to start a long-running task, consider using StartJob() instead.
        /// <br/><br/>Some networking devices may automatically disconnect TCP connections that are
        /// idle for extended periods. Enabling TCP keep alive packets on your client may help,
        /// at the expense of additional power consumption on the server. 
        /// </remarks>
        /// <param name="programName">Name of called program (exe/bat/ps1).</param>
        /// <param name="args">Arguments for the called program.</param>
        /// <returns>Standard output and error from called process.</returns>
        [SimpleRpcMethod]
        public static string RunWithResult(string programName, string args = null)
        {
            StringBuilder output = new StringBuilder();
            using (Process p = SetupProcess(programName, args, true))
            {
                DataReceivedEventHandler outputHandler = (s, a) =>
                {
                    logger.Debug($"{programName} output: {a.Data}");
                    output.AppendLine(a.Data);
                };
                p.OutputDataReceived += outputHandler;
                p.ErrorDataReceived += outputHandler;

                logger.Info("Starting program...");
                p.Start();

                p.BeginErrorReadLine();
                p.BeginOutputReadLine();

                logger.Info("Waiting for called program to complete...");
                p.WaitForExit();
                logger.Info($"Process {p.StartInfo.FileName} has completed.");
            }

            return output.ToString();
        }
        #endregion

        #region Job Functions (Non-Blocking, Callbacks)

        /// <summary>
        /// Start a job on the remote machine.
        /// </summary>
        /// <remarks>
        /// The job system allows callers to start long-running jobs on the remote machine without blocking. 
        /// When a job is started (using this function), the function returns a job id which can be used
        /// to query the status of the job, or to retrieve it's result. A given job id is valid until
        /// a call to GetJobResult(), which returns the output from the job and releases any associated
        /// resources, or a call to StopJob() which terminates a job and releases any resources.<br/><br/>
        /// Note that any output to stdout or stderr from the job will be stored in memory until GetJobResult()
        /// is called. If your job will generate a large amount of output, you may want to pipe the output
        /// to a file instead. 
        /// </remarks>
        /// <param name="programName">Name of called program (exe/bat/ps1).</param>
        /// <param name="args">Arguments for the called program.</param>
        /// <returns>Job id of newly generated job.</returns>
        [SimpleRpcMethod]
        public int StartJob(string programName, string args = null)
        {
            Process p = SetupProcess(programName, args, true);
            var newJob = Job.CreateJob(p);
            jobs[newJob.jobId] = newJob;

            return newJob.jobId;
        }

        /// <summary>
        /// Start a job on a remote machine, and receive a TCP msg on completion.
        /// </summary>
        /// <remarks>
        /// Operates identically to StartJob(), but sends a simple text message back to the client
        /// once the job is done via TCP. The message is always `JOB X COMPLETED` where X is the job number.
        /// This requires that the client have a TCP listener active on the specified port before the job finishes
        /// to receive the callback. If the job finishes and the service cannot contact open a connection to the client,
        /// it will simply log the failure, and continue to operate normally, as though the standard StartJob() function
        /// was used. 
        /// </remarks>
        /// <param name="callbackAddress">IP address of the client machine, if not specified will use data from this connection.</param>
        /// <param name="callbackPort">TCP port number on the client machine to connect to for the callback</param>
        /// <param name="programName">Name of called program (exe/bat/ps1).</param>
        /// <param name="args">Arguments for the called program.</param>
        /// <returns>Job id of the newly generated job.</returns>
        [SimpleRpcMethod]
        public int StartJobWithNotification(string callbackAddress, long callbackPort, 
            string programName, string args = null)
        {
            IPAddress callbackIp;

            if (String.IsNullOrWhiteSpace(callbackAddress))
            {
                if (SimpleRpcServer.currentClient == null || SimpleRpcServer.currentClient.Value == null)
                {
                    throw new InvalidOperationException("No callback IP provided, and unable to get current connection data.");
                }
                callbackIp = SimpleRpcServer.currentClient.Value.Address;
            }
            else
            {
                callbackIp = IPAddress.Parse(callbackAddress);
            }

            JobCallbackInfo info = new JobCallbackInfo()
            {
                Address = callbackIp,
                Port = (int) callbackPort
            };

            Process p = SetupProcess(programName, args, true);
            var newJob = Job.CreateJob(p, info);
            jobs[newJob.jobId] = newJob;

            return newJob.jobId;
        }

        /// <summary>
        /// Poll to see if a job has finished.
        /// </summary>
        /// <param name="jobId">Job id returned by CreateJob()</param>
        /// <returns>True if done, false otherwise.</returns>
        [SimpleRpcMethod]
        public bool IsJobComplete(long jobId)
        {
            Job tempJob;
            if (!jobs.TryGetValue(jobId, out tempJob))
            {
                throw new InvalidOperationException("Invalid Job Id");
            }

            return tempJob.IsDone();   
        }

        /// <summary>
        /// Halts a running job.
        /// </summary>
        /// <remarks>Terminates a job and the underlying called process. Note that if
        /// this function is successful, the job id will become invalid, and 
        /// any captured output from the job will be lost.</remarks>
        /// <param name="jobId">Job id returned by CreateJob()</param>
        /// <returns>True on success, throws exception otherwise.</returns>
        [SimpleRpcMethod]
        public bool StopJob(long jobId)
        {
            Job tempJob;
            if (!jobs.TryRemove(jobId, out tempJob))
            {
                throw new InvalidOperationException("Invalid Job Id");
            }

            if (tempJob.IsDone())
            {
                // add the job back into the dictionary, since it might have data output worth
                // capturing.
                jobs[jobId] = tempJob;

                // throw an error, since the kill operation failed.
                throw new InvalidOperationException("Job already completed - nothing to kill");
            }

            tempJob.Kill();
            return true;
        }

        /// <summary>
        /// Returns the output of a completed job.
        /// </summary>
        /// <remarks>Retrieves the output from a completed job (stdout and stderr). Once this function
        /// is completed, all resources assocated with the job will be freed, and the job id will become
        /// invalid.</remarks>
        /// <param name="jobId">Job id returned by CreateJob()</param>
        /// <returns>String of stdout and stderr from completed job.</returns>
        [SimpleRpcMethod]
        public string GetJobResult(long jobId)
        {
            Job tempJob;
            if (!jobs.TryGetValue(jobId, out tempJob))
            {
                throw new InvalidOperationException("Invalid Job Id");
            }   

            if (!tempJob.IsDone())
            {
                throw new InvalidOperationException("Job has not completed.");
            }

            var result = tempJob.GetResult();
            jobs.TryRemove(jobId, out tempJob);
            return result;
            
        }

        /// <summary>
        /// Returns all active and completed jobs.
        /// </summary>
        /// <remarks>Returns all the active jobs on the system (ones which are running) and
        /// jobs that have completed, but the result has not been read.</remarks>
        /// <returns>Dictionary of job numbers, and bools indicating if the jobs are complete.</returns>
        [SimpleRpcMethod]
        public Dictionary<long, bool> GetAllJobs()
        {
            return jobs.ToDictionary(k => k.Key, k => k.Value.IsDone());
        }

        #endregion

        #region Misc Process Control Functions (Kill, etc)

        /// <summary>
        /// Kill a process by name.
        /// </summary>
        /// <remarks>Kill a task using the image name (the name in process manager). If you are trying to start a process
        /// that was created with StartJob(), use StopJob() instead.</remarks>
        /// <param name="processName">Name of the process to stop.</param>
        /// <returns>String "OK"</returns>
        [SimpleRpcMethod]
        public static bool KillProcess(string processName)
        {
            var procs = Process.GetProcessesByName(processName);
            foreach (Process proc in procs)
            {
                proc.Kill();
            }

            // return true if any process was killed
            return true;
        }


        #endregion

        #region File Transfer

        /// <summary>
        /// Uploads a file to the server.
        /// </summary>
        /// <remarks>Writes the provided base64 encoded data to a file. The client is responsible for
        /// performing base64 encoding on the data. You must specify if files should be overwritten.
        /// <br/>This operation stores the entire file in memory while it is being received and decoded. If you are
        /// moving large files (over 1GB), you should use UploadLargeFile() instead.</remarks>
        /// <param name="filename">Location on DUT to store data.</param>
        /// <param name="data">Base64 encoded contents of the file to save.</param>
        /// <param name="overwrite">Flag to indicate if existing files should be overwritten.</param>
        /// <returns>Number of bytes written.</returns>
        [SimpleRpcMethod]
        public static int UploadFile(string filename, string data, bool overwrite)
        {
            int length = 0;
            using (var fs = File.Open(filename, overwrite ? FileMode.Create : FileMode.CreateNew))
            {
                var bytes = System.Convert.FromBase64String(data);
                length = bytes.Length;
                fs.Write(bytes, 0, bytes.Length);
            }

            return length;
        }

        /// <summary>
        /// Downloads a file from the server.
        /// </summary>
        /// <remarks>Retrieves a file, as a base64 encoded string. The client is responsible for
        /// performing base64 decoding on the data.
        /// <br/>This operation stores the entire file in memory while it is being received and encoded. If you are
        /// moving large files (over 1GB), you should use DownloadLargeFile() instead.</remarks>
        /// <param name="filename">Location on DUT to fetch.</param>
        /// <returns>Base64 encoded string, containing the contents of the file.</returns>
        [SimpleRpcMethod]
        public static string DownloadFile(string filename)
        {
            using (var fs = File.Open(filename, FileMode.Open))
            {
                var length = fs.Length;

                // ensure that we're not going to transfer something huge (or that would cause the cast below to break)
                if (length > int.MaxValue)
                {
                    throw new IOException("This file is too large to use DownloadFile. Use DownloadLargeFile instead.");
                }

                var buffer = new byte[length];
                fs.Read(buffer, 0, (int) length);

                return System.Convert.ToBase64String(buffer);
            }
        }

        /// <summary>
        /// Upload a file or folder to the server.
        /// </summary>
        /// <remarks>This method openes a port on the server, and allows the 
        /// client to directly write data to the port. It treats all data written as
        /// part of a tarfile. As data is received from the socket, it is extracted to the target path. Once the server
        /// receives the last block of the tarfile, it will send back the number of bytes written to disk.
        /// <br/>The port will timeout if a connection is not established promptly, so clients should connect to the port
        /// immediately after calling this method.</remarks>
        /// <param name="path">Path of directory to save extracted data on the remote machine.</param>
        /// <param name="overwrite">Bool indicating if existing files should be overwritten.</param>
        /// <param name="port">Optional port number to use for the transfer. If not specified, the server will let the OS choose the port number.</param>
        /// <returns>Port on remote machine</returns>
        [SimpleRpcMethod]
        public static int Upload(string path, bool overwrite, long port = 0)
        {
            if (!ReadWriteChecks.CheckWriteToDir(Directory.GetParent(path).FullName))
            {
                throw new IOException($"Can't write to {path}, there was either a permission problem or the path doesn't exist.");
            }
            TcpListener server = new TcpListener(IPAddress.Any, (int) port);

            // if a port is specified, use SO_REUSEADDR on the socket
            if (port != 0)
            {
                server.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }

            server.Start(1);
            Task.Run(() => HelperFunctions.LargeFileTransfers.Upload(path, server, overwrite));
            return ((IPEndPoint)server.LocalEndpoint).Port;
        }

        /// <summary>
        /// Download a file or folder from the server.
        /// </summary>
        /// <remarks>This method openes a port on the server, and allows the 
        /// client to directly read data from the port. All data is wrapped in a tarfile. 
        /// The client can either write the received bytes directly to a tarfile, or extract the contents. 
        /// <br/>This method supports supports glob expressions, as long as they are at the end of the path
        /// (C:\foo* and C:\foo\*.etl are valid, C:\*\bar\baz is not).
        /// <br/>The port will timeout if a connection is not established promptly, so clients should connect to the port
        /// immediately after calling this method.</remarks>
        /// <param name="path">Path to download on the remote machine.</param>
        /// <param name="port">Optional port number to use for the transfer. If not specified, the server will let the OS choose the port number.</param>        
        /// <returns>Array of two 64-bit integers, the port number, and the uncompressed size of the files/directories to transfer in bytes.</returns>
        [SimpleRpcMethod]
        public static long[] Download(string path, long port = 0)
        {
            if (!ReadWriteChecks.CheckReadFromFileOrDir(path))
            {
                throw new IOException($"Can't read from {path}, there was either a permission problem, or the path doesn't exist.");
            }

            TcpListener server = new TcpListener(IPAddress.Any, (int) port);
            
            // if a port is specified, use SO_REUSEADDR on the socket
            if (port != 0)
            {
                server.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }

            server.Start(1);

            // get length of all files in the directory we're about to download
            // (total directory size)
            long size = 0;

            if (path.Contains("*") || path.Contains("?"))
            {
                // we're handling a glob expression 
                size = SimpleDUTCommonLibrary.GlobFunctions.Glob(path)
                    .Where(x => !File.GetAttributes(x).HasFlag(FileAttributes.Directory))
                    .Select(x => (new FileInfo(x)).Length)
                    .Sum();
            }
            else if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
            {
                size = Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                    .Aggregate<string, long>(0, (acc, current) => acc + (new FileInfo(current)).Length);
            }
            else
            {
                size = (new FileInfo(path)).Length;
            }

            Task.Run(() => HelperFunctions.LargeFileTransfers.Download(path, server));
            return new long[] { ((IPEndPoint)server.LocalEndpoint).Port, size };

        }

        #endregion

        #region Version, Heartbeat, and other Utility Functions
        [SimpleRpcMethod]
        public static string GetVersion()
        {
            // this will return the assembly version for the assembly holding this class (Functions).
            // instead of the calling assembly (which is the command line app)
            var assem = IntrospectionExtensions.GetTypeInfo(typeof(Functions))
                .Assembly;
            var attribute = assem.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var ver = attribute.InformationalVersion;
            return ver;

        }

        /// <summary>
        /// Simple function to confirm the server is up. Returns true in all cases.
        /// </summary>
        /// <returns>True.</returns>
        [SimpleRpcMethod]
        public static bool GetHeartbeat()
        {
            return true;
        }

        /// <summary>
        /// Return the IP address the of the client, as seen by the server.
        /// </summary>
        /// <remarks>This may be useful for some systems where the test machine has mutliple
        /// active interfaces, and a user would like to determine which is being used by this connection.</remarks>
        /// <returns>IP Address of the client, as a string</returns>
        [SimpleRpcMethod]
        public static string GetClientIP()
        {
            if (SimpleRpcServer.currentClient == null || SimpleRpcServer.currentClient.Value == null)
            {
                throw new InvalidOperationException("There isn't an RPC server running, or a client isn't connected.");
            }

            return SimpleRpcServer.currentClient.Value.ToString();
        }

        /// <summary>
        /// Return if the current process is running as an Administrator.
        /// </summary>
        /// <remarks>This function only works on Windows. It will throw PlatformNotSupportedException
        /// on MacOS and Linux.
        /// </remarks> 
        /// <returns>True if this process is running as an administrator. False otherwise.</returns>
        [SimpleRpcMethod]
        public static bool GetIsRunningAsAdmin()
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("This function is only supported on Windows OS at this time.");
            }

            var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        #endregion

        #region Extension System
        /// <summary>
        /// Load an arbitrary class from a DLL.
        /// </summary>
        /// <remarks>In cases where calling an executable or script is undesirable, users
        /// can use this function to access an arbitrary .NET DLL, provided that it is compatible with 
        /// the running architecture and framework (use .NET DLLs for .NET Framework, .NET Core DLLs for netcore).
        /// <br/><br/>Be advised that, when copying your DLL, you <i>must</i> copy any dependent DLLs as well.
        /// <br/><br/>The class must also have a constructor that doesn't take arguments. If you need more complex object
        /// creation, create a basic instance of the class, and call a function to perform any required initialization.
        /// </remarks>
        /// <param name="identifier">A string identifier to reference the loaded class in future calls.</param>
        /// <param name="dllClass">The fully qualified name of the class (Namespace.Classname)</param>
        /// <param name="dllPath">The path to the DLL. Dependent libraries must be in the same directory.</param>
        /// <returns>Bool if successful, throws otherwise.</returns>
        [SimpleRpcMethod]
        public bool PluginLoad(string identifier, string dllClass, string dllPath)
        {
            // we're going to load an instance of this class from a DLL by name
            if (!File.Exists(dllPath)) { throw new IOException($"Unable to load plugin at path: {dllPath}; file not found"); }

            Assembly assem = Assembly.LoadFrom(dllPath);
            Type targetType = assem.GetType(dllClass);
            if (targetType == null) { throw new IOException("Could not find class with specified name in DLL."); }
            object targetClassObject = (object) Activator.CreateInstance(targetType);
            pluginObjects[identifier] = targetClassObject ?? throw new IOException($"Failed to create instance of class: ${dllClass}.");

            return true;
        }

        /// <summary>
        /// Call a function in a DLL.
        /// </summary>
        /// <remarks>Call a function in a class, loaded from a custom DLL. The class must have been loaded with
        /// PluginLoad() before calling this function.</remarks>
        /// <param name="identifier">A string identifier passed to PluginLoad() to locate the class.</param>
        /// <param name="methodName">Method to call on the loaded DLL.</param>
        /// <param name="args">Optional arguments to be passed to the called function.</param>
        /// <returns>The result from the call, or null if the function called was type void.</returns>
        [SimpleRpcMethod]
        public object PluginCallMethod(string identifier, string methodName, params object[] args)
        {
            object pluginClassObj = null;
            if (!pluginObjects.TryGetValue(identifier, out pluginClassObj)) { 
                throw new ArgumentException($"No class loaded with identifier: {identifier}"); 
            }

            var method = pluginClassObj.GetType().GetMethod(methodName);
            return method.Invoke(pluginClassObj, args);

        }

        /// <summary>
        /// Remove an reference to a plugin.
        /// </summary>
        /// <remarks>This removes the referenced class from the internal class store, allowing it to be
        /// garbage collected. <b>This function does not unload the assembly - there is no mechanism in .NET to 
        /// unload a manually loaded assembly.</b>
        /// <param name="identifier">A string identifier passed to PluginLoad() to locate the class.</param>
        /// <returns>True if successful, throws otherwise.</returns>
        [SimpleRpcMethod]
        public bool PluginUnload(string identifier)
        {
            object pluginClassObject = null;
            if (!pluginObjects.TryRemove(identifier, out pluginClassObject))
            {
                throw new ArgumentException($"No class loaded with identifier: {identifier}");
            }

            return true;
        }

        #endregion



        private static Process SetupProcess(string programName, string args = "", bool redirectOutput = false)
        {
            // first argument is an program (exe/bat/ps1) name
            var command = programName;

            var ext = System.IO.Path.GetExtension(command);
            Process p = new Process();
            p.StartInfo.UseShellExecute = false; //required by .net core

            // if we're using powershell, launch powershell and feed it the location of the script.
            if (ext == ".ps1")
            { 
                p.StartInfo.FileName = "powershell.exe";
                p.StartInfo.Arguments = $"-executionpolicy unrestricted -file \"{command}\"";
                p.StartInfo.Arguments += string.IsNullOrWhiteSpace(args) ? "" : " " + args;
            }

            // if we're using exe/bat/cmd/other, execute directly
            else
            {
                    p.StartInfo.FileName = command;
                    p.StartInfo.Arguments = String.Join(" ", args);
            }

            logger.Info($"Preparing to run '{p.StartInfo.FileName} {p.StartInfo.Arguments}'");

            // determine if we're redirecting stdout/stderr back to the caller. 
            // if not, just return the process object.
            if (!redirectOutput){
                return p;
            }
            // if we are, we need to set it up.
            else
            {
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.RedirectStandardOutput = true;
                return p;
            }

        }
    }
}
