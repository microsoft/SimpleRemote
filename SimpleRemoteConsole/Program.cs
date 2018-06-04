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

namespace SimpleRemoteConsole
{
    class Program
    {
        static AutoResetEvent stopEvt = new AutoResetEvent(false);

        static void Main(string[] args)
        {
            if (args.Contains("--SuppressUserWarning"))
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

            Logger logger = LogManager.GetCurrentClassLogger();


            Console.CancelKeyPress += HandleCancelEvent;

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
            var serverTask = server.Start(portNumber, null, broadcastPort);

            // wait for Ctrl+C
            stopEvt.WaitOne();

            Console.WriteLine("Stopping Server.");
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