// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using SimpleDUTCommonLibrary;
using System.Threading.Tasks;
using System.Reflection;
using System.Text;

namespace SimpleDUTClientLibrary
{
    /// <summary>
    /// Client class for Simple DUT Remote. 
    /// </summary>
    /// <remarks>Once created, the RpcClient class enables consumers of this library to easily
    /// control a remote system running the associated server program. 
    /// </remarks>
    public class RpcClient
    {
        private IPAddress serverAddress;
        private int serverPort;
        private int timeout;

        private AssemblyRedirectResolver resolver;

        /// <summary>
        /// Create a new RPC client.
        /// </summary>
        /// <remarks>Note that creating a client object does not connect to the server - 
        /// it simply gathers all the required information for future connections. </remarks>
        /// <param name="serverAddress">IP address or hostname of rpc server.</param>
        /// <param name="serverPort">Server port number.</param>
        /// <param name="timeout">Timeout (in ms) for all send and receive operations.</param>
        /// <exception cref="TimeoutException">Thown if unable to resolve hostname within 10 seconds.</exception>
        /// <exception cref="SocketException">Thownn if DNS lookup fails</exception>
        public RpcClient(string serverAddress, int serverPort, int timeout = 5000)
        {
            this.serverPort = serverPort;
            this.timeout = timeout;
            if (!IPAddress.TryParse(serverAddress, out this.serverAddress))
            {
                // lookup hostname if we can't parse out an ip address
                var dnslookup = Dns.GetHostAddressesAsync(serverAddress);
                if (!dnslookup.Wait(timeout))
                {
                    throw new TimeoutException("Unable to resolve hostname to IP address.");
                }
                this.serverAddress = dnslookup.Result.Where(x => x.AddressFamily == AddressFamily.InterNetwork).FirstOrDefault();
                if (this.serverAddress == null)
                {
                    throw new ArgumentException("No IPv4 address for this hostname.");
                }
            }

            // handle binding redirection 
            DirectoryInfo AssemblyDirectory = new FileInfo(typeof(RpcClient).Assembly.Location).Directory;
            resolver = new AssemblyRedirectResolver(AssemblyDirectory);

            AppDomain.CurrentDomain.AssemblyResolve += (s, e) => resolver.ResolveAssembly(e.Name, e.RequestingAssembly);

        }

        /// <summary>
        /// Run a command on the remote machine, and return immediately.
        /// </summary>
        /// <param name="command">Path to command to run (bat/exe/ps1).</param>
        /// <param name="args">Arguments to pass to the command.</param>
        /// <returns>True if successful, throws exception otherwise.</returns>
        public bool Run(string command, string args = null)
        {
            var methodArgs = new string[] { command, args };
            return CallRpc<bool>("Run", methodArgs);
        }

        /// <summary>
        /// Run a command on the remote machine, wait for completion, and return stdout and stderr.
        /// </summary>
        /// <remarks>Starts a process, and returns standard output and standard error once the
        /// process completes. It internally uses the job system and callbacks to start a process. 
        /// It then can automatically poll for completion, or wait for a callback. You can also call
        /// RunJobAsync() for a non-blocking, cancellable version of this function.
        /// </remarks>
        /// <param name="command">Path to command to run (bat/exe/ps1).</param>
        /// <param name="args">Arguments to pass to the command.</param>
        /// <param name="pollingInterval">Polling frequency in seconds, or 0 to only rely on callbacks.</param>
        /// <param name="timeout">Time in seconds before throwing a TimeoutException, or 0 for no timeout.</param>
        /// <param name="pollingDelay">Delay until polling begins, or 0 to begin polling immediately.
        /// Ignored if the polling interval is 0.</param>
        /// <returns>String of standard output and standard error.</returns>
        public string RunJob(string command, string args = null,
                                    int pollingInterval = 60,
                                    int timeout = 0,
                                    int pollingDelay = 60)
        {
            var tsk = _RunJob(command, args, pollingInterval, timeout, pollingDelay);
            tsk.Wait();
            return tsk.Result;
        }

        /// <summary>
        /// Runs a job on the remote machine without blocking, and return the result.
        /// </summary>
        /// <remarks>This command is similar to RunJob(), but returns a task
        /// instead of a string, and accepts cancellation tolkens. 
        /// It can be considered a simpler way to launch processes
        /// on the remote without worrying about jobs, callbacks, or timeouts.</remarks>
        /// <param name="command">Path to command to run (bat/exe/ps1).</param>
        /// <param name="args">Arguments to pass to the command.</param>
        /// <param name="pollingInterval">Polling frequency in seconds, or 0 to only rely on callbacks.</param>
        /// <param name="timeout">Time in seconds before throwing a TimeoutException, or 0 for no timeout.</param>
        /// <param name="pollingDelay">Delay until polling begins, or 0 to begin polling immediately.
        /// Ignored if the polling interval is 0.</param>
        /// <param name="cancellationToken">Optional cancellation token for the task. Can be used to cancel the job.</param>
        /// <returns>Task, populated with the stdout and stderr of the called process on completion.</returns>
        public Task<string> RunJobAsync(string command, string args = null,
                                                     int pollingInterval = 60,
                                                     int timeout = 0,
                                                     int pollingDelay = 60,
                                                     CancellationToken? cancellationToken = null)
        {
            return _RunJob(command, args, pollingInterval, timeout, pollingDelay, cancellationToken);
        }

        /// <summary>
        /// Start a job on the remote system.
        /// </summary>
        /// <remarks>Starts a program on the remote system, and returns a job id,
        /// which can be used to track the program and retrieve its output. RunJob()
        /// and RunJobAsync() provide a higher level interface to the job system.</remarks>
        /// <param name="command">Path to command to run (bat/exe/ps1).</param>
        /// <param name="args">Arguments to pass to the command.</param>
        /// <returns>Job id for the newly created job.</returns>
        public int StartJob(string command, string args = null)
        {
            var methodArgs = new string[] { command, args };
            return CallRpc<int>("StartJob", methodArgs);
        }

        /// <summary>
        /// Start job, and call a provided callback function once it completes.
        /// </summary>
        /// <param name="command">Path to command to run (bat/exe/ps1).</param>
        /// <param name="args">Arguments to pass to the command (use null or empty string for no args).</param>
        /// <param name="callback">Callback to run once job completes - the completed job number will be provided as a argument to the function.</param>
        /// <param name="localIP">Local IP address to use for inbound notification. If null, will let server determine this machine's address.</param>
        /// <returns>Job id for the newly created job.</returns>
        public int StartJobWithNotification(string command, string args, Action<int> callback, IPAddress localIP = null)
        {
            // setup listener first
            TcpListener listener = new TcpListener(IPAddress.Any, 0);
            listener.Start(1);
            var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            // this task will be fired when the server connects back to the client.
            listener.AcceptTcpClientAsync().ContinueWith((t) =>
            {
                try
                {
                    var client = t.Result;
                    string msg;

                    using (client)
                    using (StreamReader cbReader = new StreamReader(client.GetStream()))
                    {
                        msg = cbReader.ReadToEnd();
                    }

                    // we know the message is "JOB X COMPLETED" so just split on space, and parse the middle.
                    int jobNumber = int.Parse(msg.Split(' ')[1]);
                    callback(jobNumber);
                }
                finally
                {
                    // always close the listener - we no longer need it. 
                    listener.Stop();
                }
            });

            var methodArgs = new object[] { localIP, localPort, command, args };
            return CallRpc<int>("StartJobWithNotification", methodArgs);
        }

        /// <summary>
        /// Start job, and stream standard out from the called process to the client.
        /// </summary>
        /// <remarks>You should call GetJobResult() on the completed job, even though there will be no output, to acknowledge
        /// to the server that you're done with the job and to release resources associated with it on the server.
        /// <br/><br/>
        /// Because this will result in both network and disk activity on the server, it is not recommended
        /// for use while performing power sensitive measurements.
        /// </remarks>
        /// <param name="command">Path to command to run (bat/exe/ps1).</param>
        /// <param name="args">Arguments to pass to the command (use null or empty string for no args).</param>
        /// <param name="progressCallback">Callback to run whenever the called process emits a line of text. Emitted text will be an argument for the function.</param>
        /// <param name="completionCallback">Callback to run once job completes - the completed job number will be provided as a argument to the function.</param>
        /// <param name="localIP">Local IP address to use for inbound notification. If null, will let server determine this machine's address.</param>
        /// <returns>Job id for the newly created job.</returns>
        public int StartJobWithProgress(string command, string args, Action<string> progressCallback, Action<int> completionCallback, IPAddress localIP = null)
        {
            // setup completion listener first
            TcpListener completionListener = new TcpListener(IPAddress.Any, 0);
            completionListener.Start(1);
            var localCompletionPort = ((IPEndPoint)completionListener.LocalEndpoint).Port;

            // this task will be fired when the server connects back to the client.
            completionListener.AcceptTcpClientAsync().ContinueWith((t) =>
            {
                try
                {
                    var client = t.Result;
                    string msg;

                    using (client)
                    using (StreamReader cbReader = new StreamReader(client.GetStream()))
                    {
                        msg = cbReader.ReadToEnd();
                    }

                    // we know the message is "JOB X COMPLETED" so just split on space, and parse the middle.
                    int jobNumber = int.Parse(msg.Split(' ')[1]);
                    completionCallback?.Invoke(jobNumber);
                }
                finally
                {
                    // always close the listener - we no longer need it. 
                    completionListener.Stop();
                }
            });

            // setup progress listener next
            TcpListener progressListener = new TcpListener(IPAddress.Any, 0);
            progressListener.Start(1);
            var localProgressPort = ((IPEndPoint)progressListener.LocalEndpoint).Port;

            progressListener.AcceptTcpClientAsync().ContinueWith((t) => 
            {
                try
                {
                    var client = t.Result;
                    string msg;

                    using (client)
                    using (StreamReader progressReader = new StreamReader(client.GetStream()))
                    {
                        while ((msg = progressReader.ReadLine()) != null)
                        {
                            progressCallback?.Invoke(msg);
                        }
                    }
                }
                finally
                {
                    progressListener.Stop();
                }
            });

            var methodArgs = new object[] { localIP, localCompletionPort, localProgressPort, command, args };
            return CallRpc<int>("StartJobWithProgress", methodArgs);
        }

        /// <summary>
        /// Determine if a job has finished executing.
        /// </summary>
        /// <param name="jobId">Job id provided by StartJob()</param>
        /// <returns>True if job is finished, false otherwise.</returns>
        public bool CheckJobCompletion(int jobId)
        {
            return CallRpc<bool>("IsJobComplete", new object[] { jobId });
        }

        /// <summary>
        /// Return all jobs on the remote system.
        /// </summary>
        /// <remarks>Return all jobs that are either running, or are completed, but their
        /// output hasn't been read yet.</remarks>
        /// <returns>Dictionary containing job ids, and a bool indicating if the job has completed.</returns>
        public Dictionary<long, bool> GetAllJobs()
        {
            return CallRpc<Dictionary<long, bool>>("GetAllJobs");
        }

        /// <summary>
        /// Get the output from a completed job (stdout + stderr).
        /// </summary>
        /// <param name="jobId">Job id provided by StartJob()</param>
        /// <returns>Standard output and error from the job.</returns>
        public string GetJobResult(int jobId)
        {
            return CallRpc<string>("GetJobResult", new object[] { jobId });
        }

        /// <summary>
        /// Stop a job and kill the underlying process on the remote machine.
        /// </summary>
        /// <param name="jobId">Job id provided by StartJob()</param>
        /// <returns>True on success. Throws otherwise.</returns>
        public bool StopJob(int jobId)
        {
            return CallRpc<bool>("StopJob", new object[] { jobId });
        }

        /// <summary>
        /// Upload a directory or file to the server.
        /// </summary>
        /// <remarks><b>Warning for Powershell users: </b>Powershell has two "current directories", there's the one you
        /// see on your terminal (what you see with cd/pwd), and the current directory of the Powershell process (where
        /// you started Powershell, likely your user profile). Unfortunately, when a C# DLL calls `GetCurrentDirectory`, it
        /// gets the process directory. If you try to upload a file, and you get an unexpected `System.IO.DirectoryNotFoundException` 
        /// from the server, there's a good chance you've hit this. To resolve, either use forward slashes in your filenames, 
        /// or explicitly set the process directory using `[Environment]::CurrentDirectory = get-location`.
        /// <br/><br/>See https://stackoverflow.com/questions/18862716/current-directory-from-a-dll-invoked-from-powershell-wrong for 
        /// additional information.</remarks>
        /// <param name="localPath">Path to the directory or file on this machine to send to the server.</param>
        /// <param name="remoteDirPath">Location on the server to write data (should be a directory, regardless if sending files).</param>
        /// <param name="overwrite">Indicates if the file should be overwritten on the server, if it already exists.</param>
        /// <param name="portNumber">If provided, request the server use the specified port for the transfer.</param>
        /// <returns>Size (in bytes) of files uploaded.</returns>
        public long Upload(string localPath, string remoteDirPath, bool overwrite = false, int portNumber = 0)
        {
            // this method will always use UploadLargeFile, regardless of local file size.
            int transferPort = CallRpc<int>("Upload", new object[] { remoteDirPath, overwrite, portNumber });

            using (TcpClient uploadClient = new TcpClient())
            {
                if (!uploadClient.ConnectAsync(this.serverAddress, transferPort).Wait(timeout))
                {
                    throw new TimeoutException("Could not connect to server's listening port to upload large file.");
                }

                using (NetworkStream nstream = uploadClient.GetStream())
                using (var sr = new StreamReader(nstream))
                {
                    // the file system binding can be a bit unstable if calling directly from the DLL
                    // redirect it manually to the filesystem DLL alongside the client. 
                    var bytesSent = TarFunctions.WriteFileOrDirectoryToStream(nstream, localPath, closeStreamWhenComplete: false);

                    // let's try to read the number of bytes received.
                    long bytesRecvByRemote;
                    var bytesAsStr = sr.ReadToEnd().Trim();
                    bytesRecvByRemote = Int64.Parse(bytesAsStr);

                    if (bytesSent != bytesRecvByRemote)
                        throw new IOException($"Sent {bytesSent} bytes, but server received {bytesRecvByRemote}");

                    return bytesRecvByRemote;
                }

            }
        }

        /// <summary>
        /// Upload a file or folder to the server without blocking.
        /// </summary>
        /// <remarks><b>Warning for Powershell users: </b>Powershell has two "current directories", there's the one you
        /// see on your terminal (what you see with cd/pwd), and the current directory of the Powershell process (where
        /// you started Powershell, likely your user profile). Unfortunately, when a C# DLL calls `GetCurrentDirectory`, it
        /// gets the process directory. If you try to upload a file, and you get an unexpected `System.IO.DirectoryNotFoundException` 
        /// from the server, there's a good chance you've hit this. To resolve, either use forward slashes in your filenames, 
        /// or explicitly set the process directory using `[Environment]::CurrentDirectory = get-location`.
        /// <br/><br/>See https://stackoverflow.com/questions/18862716/current-directory-from-a-dll-invoked-from-powershell-wrong for 
        /// additional information.</remarks>
        /// <param name="localPath">Path to the file or folder on this machine to send to the server.</param>
        /// <param name="remoteDirPath">Location on the server to write the file or folder (should be a directory, regardless if sending files).</param>
        /// <param name="overwrite">Indicates if the file should be overwritten on the server, if it already exists.</param>
        /// <param name="portNumber">If provided, request the server use the specified port for the transfer.</param>
        /// <returns>Task, returns size (in bytes) of files uploaded once completed.</returns>
        public Task<long> UploadAsync(string localPath, string remoteDirPath, bool overwrite = false, int portNumber = 0)
        {
            return Task.Run(() => Upload(localPath, remoteDirPath, overwrite, portNumber));
        }

        /// <summary>
        /// Download a file or folder from the server.
        /// </summary>
        /// <param name="remotePath">Path to file or folder on server.</param>
        /// <param name="localDirPath">Path to write received file on local machine (should be a directory, regardless if receiving files).</param>
        /// <param name="overwrite">Indicates if a file should be overwritten, if it already exists.</param>
        /// <param name="portNumber">If provided, request the server use the specified port for the transfer.</param>
        /// <returns>Size (in bytes) of files downloaded.</returns>
        public long Download(string remotePath, string localDirPath, bool overwrite = false, int portNumber = 0)
        {
            long[] transferInfo = CallRpc<long[]>("Download", new object[] { remotePath, portNumber });
            long bytesReceived = 0;

            using (TcpClient uploadClient = new TcpClient())
            {
                if (!uploadClient.ConnectAsync(this.serverAddress, (int)transferInfo[0]).Wait(timeout))
                {
                    throw new TimeoutException("Could not connect to server's listening port to upload large file.");
                }

                using (NetworkStream nstream = uploadClient.GetStream())
                {
                    // the file system binding can be a bit unstable if calling directly from the DLL
                    // redirect it manually to the filesystem DLL alongside the client. 
                    bytesReceived = TarFunctions.ReadFileOrDirectoryFromStream(nstream, localDirPath, overwrite, closeStreamWhenComplete: false);

                    // in the event that there are extra bytes in the receive buffer (tar sent extra null blocks),
                    // clear them out before shutting down the socket.
                    var dummyBuffer = new byte[1024];
                    while (nstream.DataAvailable)
                    {
                        nstream.Read(dummyBuffer, 0, 1024);
                    }
                }

            }

            if (transferInfo[1] != bytesReceived) //sanity check
            {
                throw new IOException($"Expected {transferInfo[1]} bytes, received {bytesReceived}");
            }

            return bytesReceived;
        }

        /// <summary>
        /// Download a file or folder from the server without blocking.
        /// </summary>
        /// <param name="remotePath">Path to file or folder on server.</param>
        /// <param name="localDirPath">Path to write received file on local machine (should be a directory, regardless if receiving a file).</param>
        /// <param name="overwrite">Indicates if a file should be overwritten, if it already exists.</param>
        /// <param name="portNumber">If provided, request the server use the specified port for the transfer.</param>
        /// <returns>Task, returns size of files downloaded (in bytes) once it completes.</returns>
        public Task<long> DownloadAsync(string remotePath, string localDirPath, bool overwrite = false, int portNumber = 0)
        {
            return Task.Run(() => Download(remotePath, localDirPath, overwrite, portNumber));
        }

        /// <summary>
        /// Kill a running process on the remote machine.
        /// </summary>
        /// <param name="processName">Name of the process, as identified by task manager's detailed view.</param>
        /// <returns>True on success. Throws otherwise.</returns>
        public bool KillProcess(string processName)
        {
            return CallRpc<bool>("KillProcess", new string[] { processName });
        }

        /// <summary>
        /// Return the server's version number.
        /// </summary>
        /// <returns></returns>
        public string GetVersion()
        {
            return CallRpc<string>("GetVersion");
        }

        /// <summary>
        /// Get a heartbeat from the server.
        /// </summary>
        /// <remarks>Returns true if the server responded to the heartbeat command. Throws otherwise.</remarks>
        public bool GetHeartbeat()
        {
            return CallRpc<bool>("GetHeartbeat");
        }

        /// <summary>
        /// Return if the server process is running as an Administrator.
        /// </summary>
        /// <remarks>This function only works if the server is running on a Windows system. It
        /// will throw a PlatformNotSupportedException if the server is running on Linux or MacOS.
        /// </remarks>
        /// <returns>True if server is running as an administrator. False otherwise.</returns>
        public bool GetIsRunningAsAdmin()
        {
            return CallRpc<bool>("GetIsRunningAsAdmin");
        }

        /// <summary>
        /// Load a plugin on the server.
        /// </summary>
        /// <remarks>Load a class from a .NET (or .NET Core) DLL on the remote server. This allows you
        /// to call arbitrary functions from the DLL using the PluginCallMethod() function. 
        /// Note that the plugin's framework (.NET or .NET Core) must match the framework of
        /// the running server. Additionally, the class object created must have a public,
        /// parameter-less constructor.</remarks>
        /// <param name="identifier">A string to identify the DLL on subsequent method calls.</param>
        /// <param name="className">Full name of class to create (Namespace.Class).</param>
        /// <param name="dllPath">Path to DLL containing the given class.</param>
        /// <returns>Returns true if successful, throws otherwise.</returns>
        public bool PluginLoad(string identifier, string className, string dllPath)
        {
            return CallRpc<bool>("PluginLoad", new string[] { identifier, className, dllPath });
        }

        /// <summary>
        /// Call a method on a loaded plugin.
        /// </summary>
        /// <remarks>Call an arbitrary method on a plugin-loaded class. Before calling this
        /// method, you must load the plugin and create an instance of a class with PluginLoad().
        /// <br/><br/>Note: If the called function returns void, set the return type to object.
        /// </remarks>
        /// <typeparam name="T">Return type of the called function.</typeparam>
        /// <param name="identifier">Identifier given to PluginLoad()</param>
        /// <param name="methodName">Method to call on the class.</param>
        /// <param name="args">Arguments to be fed to the called method.</param>
        /// <returns>Object of type T, returned from called function.</returns>
        public T PluginCallMethod<T>(string identifier, string methodName, params object[] args)
        {
            // determine the length of the object array to send.
            var argList = new object[2 + (args != null ? args.Length : 0)];
            argList[0] = identifier;
            argList[1] = methodName;
            args?.CopyTo(argList, 2);
            return CallRpc<T>("PluginCallMethod", argList);
        }

        /// <summary>
        /// Unload a loaded plugin class.
        /// </summary>
        /// <remarks>Given the same identifier used to instantiate a class with PluginLoad(), 
        /// remove references to the loaded class on the server, allowing it to be garbage collected.
        /// <br/><br/>Note: this does <u>not</u> unload the assembly from the running server's app domain,
        /// it simply allows the spawned class to be garbage collected.</remarks>
        /// <param name="identifier">Identifier given to PluginLoad()</param>
        /// <returns>True if successful, throws otherwise.</returns>
        public bool PluginUnload(string identifier)
        {
            return CallRpc<bool>("PluginUnload", new string[] { identifier });
        }

        /// <summary>
        /// Get all systems running SimpleRemote on this subnet.
        /// </summary>
        /// <remarks>Send a UDP broadcast packet to the current network, and return an array
        /// of IPEndPoint objects, each containing the IPAddress and Port of a system running
        /// SimpleRemote. 
        /// </remarks>
        /// <param name="broadcastPort">Port to use for the UDP broadcast.</param>
        /// <param name="broadcastAddress">IP Address to use for the broadcast. Defaults to 255.255.255.255</param>
        /// <param name="timeToWait">Time to wait for all responses before returning (in milliseconds)</param>
        /// <param name="localAdapterAddress">IP Address of the local adapter to use for the broadcast. Only needed if
        /// you have multiple active network adapters on the system.</param>
        /// <returns>An array of IPEndPoints, one for each SimpleRemote server.</returns>
        public static IPEndPoint[] GetAllServersOnSubnet(int broadcastPort = 8001, IPAddress broadcastAddress = null, 
            int timeToWait = 5000, IPAddress localAdapterAddress = null)
        {
            UdpClient client;
            var servers = new HashSet<IPEndPoint>();

            broadcastAddress = broadcastAddress ?? IPAddress.Broadcast;
            var broadcastEndpoint = new IPEndPoint(broadcastAddress, broadcastPort);

            // setup the client using an automatically set port. 
            // bind to a specific adapter if needed. 
            if (localAdapterAddress != null)
            {
                var localEp = new IPEndPoint(localAdapterAddress, 0);
                client = new UdpClient(localEp);
            }
            else
            {
                client = new UdpClient(0);
            }

            client.EnableBroadcast = true;

            // setup a thread to handle receive operations
            // we don't know how many servers are out there, so we'll listen for a max of timeToWait milliseconds.
            var receiverTask = Task.Run(() =>
            {
                var timeoutTask = Task.Delay(timeToWait);
                while (true)
                {
                    var udpReceiveTask = client.ReceiveAsync();
                    Task.WaitAny(timeoutTask, udpReceiveTask);
                    if (timeoutTask.IsCompleted) return;

                    // this is a message for us
                    var serverPort = BitConverter.ToInt32(udpReceiveTask.Result.Buffer, 0);

                    // construct our new endpoint object
                    var endpt = new IPEndPoint(udpReceiveTask.Result.RemoteEndPoint.Address, serverPort);
                    servers.Add(endpt);
                }

            });

            // send our ping.
            var msg = Encoding.ASCII.GetBytes("SimpleJsonRpc Ping");
            client.Send(msg, msg.Length, broadcastEndpoint);

            // wait for responses
            receiverTask.Wait();

            // close our socket
            client.Close();

            return servers.ToArray();
        }


        private string BuildRequest(string method, object[] args = null, int requestId = 1)
        {
            JObject req = new JObject();
            req["jsonrpc"] = "2.0";
            req["method"] = method;
            req["params"] = args != null && args.Length > 0 ? new JArray(args) : new JArray();
            req["id"] = requestId;

            return req.ToString(Formatting.None) + "\r\n";
        }

        private T ParseResponse<T>(string jsonString)
        {
            JObject o = JObject.Parse(jsonString);
            if (o["result"] != null) { return o["result"].ToObject<T>(); }
            // there are some functions (like DLL method calls) that might return null, even when successful.
            else if (o["result"] == null && o["error"] == null) { return default(T); }
            else { throw new Exception(o["error"].ToObject<string>()); }
        }

        private TcpClient GetClient()
        {
            // return a connection to the current server
            var client = new TcpClient();
            if (!client.ConnectAsync(this.serverAddress, this.serverPort)
                    .Wait(this.timeout))
            {
                // time out
                throw new TimeoutException("Timed out attempting to reach server.");
            }

            return client;
        }

        private T CallRpc<T>(string method, object[] args = null)
        {
            string respString;

            using (TcpClient client = GetClient())
            using (StreamWriter writer = new StreamWriter(client.GetStream()))
            using (StreamReader reader = new StreamReader(client.GetStream()))
            {
#if !DEBUG
                client.ReceiveTimeout = (timeout);
                client.SendTimeout = (timeout);
#endif
                var req = BuildRequest(method, args);
                writer.Write(req);
                writer.Flush();

                respString = reader.ReadToEnd();
            }

            return ParseResponse<T>(respString);
        }


        /// <summary>
        /// Internal - Runs a job on the remote machine without blocking, and return the result.
        /// </summary>
        /// <remarks>Internal function that powers RunWithResult - written as a private function so that
        /// RunWithResult and RunWithResultAsync can have different signatures.</remarks>
        /// <param name="command">Path to command to run (bat/exe/ps1).</param>
        /// <param name="args">Arguments to pass to the command.</param>
        /// <param name="pollingInterval">Polling frequency in seconds, or 0 to only rely on callbacks.</param>
        /// <param name="timeout">Time in seconds before throwing a TimeoutException, or 0 for no timeout.</param>
        /// <param name="pollingDelay">Delay until polling begins, or 0 to begin polling immediately.
        /// Ignored if the polling interval is 0.</param>
        /// <param name="cancellationToken">Optional cancellation token for the task. Can be used to cancel the job.</param>
        /// <returns>Task, populated with the stdout and stderr of the called process on completion.</returns>
        private async Task<string> _RunJob(string command, string args = null,
                                    int pollingInterval = 60,
                                    int timeout = 0,
                                    int pollingDelay = 60,
                                    CancellationToken? cancellationToken = null)
        {
            Timer pollingTimer = null;
            Timer timeoutTimer = null;
            TaskCompletionSource<bool> taskCompletionSource = new TaskCompletionSource<bool>();
            Action<int> completionCallback = (x) => taskCompletionSource.TrySetResult(true);

            var jobid = StartJobWithNotification(command, args, completionCallback);

            if (pollingInterval > 0)
            {
                // we need to setup polling
                TimerCallback pollingAction = (o) =>
                {
                    try
                    {
                        if (CheckJobCompletion(jobid))
                        {
                            pollingTimer.Change(Timeout.Infinite, Timeout.Infinite);
                            taskCompletionSource.TrySetResult(true);
                        }
                    }
                    catch (Exception e)
                    {
                        taskCompletionSource.TrySetException(e);
                    }
                };

                pollingTimer = new Timer(pollingAction, null, pollingInterval * 1000, pollingDelay * 1000);
            }
            if (timeout > 0)
            {
                timeoutTimer = new Timer((o) => { taskCompletionSource.SetException(new TimeoutException("RunWithResult timed out.")); }, null, timeout * 1000, Timeout.Infinite);
            }

            var tsk = taskCompletionSource.Task;
            var token = cancellationToken ?? CancellationToken.None;
            token.Register(() => taskCompletionSource.TrySetCanceled());

            try
            {
                // prevents deadlock if someone calls RunJob on a UI thread. 
                await tsk.ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (OperationCanceledException e)
            {
                StopJob(jobid);
                // don't throw until we clean up the timers. 
            }

            pollingTimer?.Dispose();
            timeoutTimer?.Dispose();

            // if we were canceled, throw cancelation exception here
            token.ThrowIfCancellationRequested();

            if (taskCompletionSource.Task.IsFaulted)
            {
                throw taskCompletionSource.Task.Exception;
            }

            return GetJobResult(jobid);
        }
    }
}