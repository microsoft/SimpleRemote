// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using SimpleDUTRemote;
using System;
using System.Net;
using System.Threading;
using NLog;
using NLog.Config;
using System.Linq;
using System.Net.NetworkInformation;
using System.Collections.Generic;
using SimpleJsonRpc;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using SimpleRemoteConsole.ServiceInterop;

namespace SimpleRemoteConsole
{
    class Program
    {
        static AutoResetEvent stopEvt = new AutoResetEvent(false);
        static Task serverTask;

        static void Main(string[] args)
        {
            if (args.Contains("--SuppressUserWarning") || args.Contains("--start-service"))
            {
                Console.WriteLine("User warning suppressed.");
            }
            else if (CheckUserWarning())
            {
                Console.WriteLine("User warning acknowledged. Proceeding...");
            }
            else
            {
                Console.WriteLine("Aborting - user declined to proceed after warning.");
                return;
            }

            const string svcName = "SimpleDUTRemote-Service";
            List<string> argsList = new List<string>(args);
            ServiceInfo info = new ServiceInfo()
            {
                ServiceName = svcName,
                DisplayName = svcName,
                BinaryPath = Assembly.GetEntryAssembly().Location,
                ServiceArgs = argsList.ToArray(),
                StartHandler = InitializeServer,
                StopHandler = () => { },
                StartType = StartType.SERVICE_DEMAND_START
            };

            try
            {
                // installs and launches the service. It doesn't fail
                // if the service is already installed and does not
                // uninstall then reinstall the service; it will just launch
                // the existing service
                if (args.Contains("--install-service"))
                {
                    // change the argument from install to start so
                    // that when the service is started and this method
                    // is re-entered, it takes the start service path
                    argsList.Remove("--install-service");
                    argsList.Add("--start-service");
                    info.ServiceArgs = argsList.ToArray();

                    var svcStartIndex = argsList.IndexOf("--service-start-type");
                    if (svcStartIndex != -1 && argsList[svcStartIndex + 1].ToLower() == "auto")
                    {
                        info.StartType = StartType.SERVICE_AUTO_START;
                    }

                    Service svc = new Service(info);
                    svc.CreateService();
                }
                else if (args.Contains("--uninstall-service"))
                {
                    Service svc = new Service(info);
                    svc.RemoveService();
                }
                else if (args.Contains("--start-service"))
                {
                    Service svc = new Service(info);
                    svc.StartService();
                }
                // if there are no service related commands, we launch the server as usual
                else
                {
                    InitializeServer(args);

                    Console.CancelKeyPress += HandleCancelEvent;
                    // wait for Ctrl+C
                    stopEvt.WaitOne();

                    Console.WriteLine("Stopping Server.");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception occurred: " + e.Message);
                Console.WriteLine("Exiting...");
            }
        }

        static void InitializeServer(string[] args)
        {
            Logger logger = LogManager.GetCurrentClassLogger();

            // determine server port number (default to 8000 unless --port is specified)
            int portNumber = 8000;
            int broadcastPort;

            if (args.Contains("--port"))
            {
                var portArgNumber = Array.IndexOf(args, "--port") + 1;
                int.TryParse(args[portArgNumber], out portNumber);
            }

            // determine broadcast port (default to server port + 1 unless --broadcastPort is specified)
            if (args.Contains("--broadcastPort"))
            {
                var bcastArgNumber = Array.IndexOf(args, "--broadcastPort") + 1;
                int.TryParse(args[bcastArgNumber], out broadcastPort);
            }
            else
                broadcastPort = portNumber + 1;
            
            Console.WriteLine("Starting Simple Remote on this system...");
            Console.WriteLine($"You can connect on {Dns.GetHostName()}:{portNumber}");
            foreach (var ip in GetLocalIPAddresses())
            {
                Console.WriteLine($"You can also use {ip.Item2}:{portNumber} ({ip.Item1})");
            }
            Console.WriteLine();

            // create our object that has our functions
            var remotes = new Functions();

            // create our server object and register our functions
            var server = new SimpleRpcServer();
            server.Register(remotes);

            Console.WriteLine("Now ready for connections; press Ctrl+C to exit.");
            serverTask = server.Start(portNumber, null, broadcastPort);
        }

        // return list of tuples containing the interface name and the IP address as a string.
        static IEnumerable<Tuple<string, string>> GetLocalIPAddresses()
        {
            var interfaceAndUnicastCollectionTuples = NetworkInterface.GetAllNetworkInterfaces()
                        .Where(x => x.OperationalStatus == OperationalStatus.Up)
                        .Where(x => x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                        .Select(x => Tuple.Create(x, x.GetIPProperties().UnicastAddresses));

            List<Tuple<string, string>> ips = new List<Tuple<string, string>>();

            // filter all ip addresses so we only collect IPv4
            foreach (var interfaceAndCollection in interfaceAndUnicastCollectionTuples)
            {
                foreach(var ip in interfaceAndCollection.Item2)
                {
                    if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ips.Add(Tuple.Create(interfaceAndCollection.Item1.Name, ip.Address.ToString()));
                }
            }

            return ips;
        }

        static bool CheckUserWarning()
        {
            var pathToThisExe = new FileInfo(Assembly.GetEntryAssembly().Location).DirectoryName;
            var pathToAckFile = Path.Combine(pathToThisExe, "UserWarningAcknowledged");
            var pathToWarnTxt = Path.Combine(pathToThisExe, "UserWarning.txt");

            if (File.Exists(pathToAckFile))
            {
                // user acknowledged warning already (or preset it)
                return true;
            }
            else
            {
                // show the warning
                var warningText = File.ReadAllText(pathToWarnTxt);
                Console.Write(warningText);

                // get user response
                ConsoleKeyInfo resp;
                while (true)
                {
                    resp = Console.ReadKey();
                    if (! new[] { 'Y', 'y', 'N', 'N'}.Contains(resp.KeyChar))
                    {
                        Console.WriteLine();
                        Console.Write("Please enter Y or N: ");

                    }
                    else break;
                }

                if (resp.KeyChar == 'y' || resp.KeyChar == 'Y')
                {
                    // user has acknowledged risk. Proceed and don't ask again
                    File.Create(pathToAckFile).Dispose(); // close the file immediately.
                    Console.WriteLine();
                    return true;
                }
                else
                {
                    Console.WriteLine();
                    return false;
                }
            }
        }

        private static void HandleCancelEvent(object sender, ConsoleCancelEventArgs args)
        {
            // don't terminate the process, we'll do that with our own event.
            args.Cancel = true;

            // only stop on ctrl+c, not ctrl+break
            if (args.SpecialKey == ConsoleSpecialKey.ControlC)
            {
                stopEvt.Set();
            }

        }
    }
}