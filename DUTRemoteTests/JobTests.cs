// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SimpleDUTRemote;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using SimpleJsonRpc;

namespace DUTRemoteTests
{
    [TestClass]
    public class JobTests
    {
        Functions remoteFunctions;

        public JobTests()
        {
            remoteFunctions = new Functions();
        }

        [TestMethod]
        public void Job_StartJob()
        {
            var id = remoteFunctions.StartJob("notepad.exe");
            Assert.IsFalse(remoteFunctions.IsJobComplete(id));

            // confirm IE started
            var procList = Process.GetProcessesByName("notepad");
            Assert.IsTrue(procList.Length > 0, "No notepad instance found.");
            Assert.IsTrue(procList.First().HasExited == false);

            // confirm job is marked as running
            Assert.IsTrue(!remoteFunctions.IsJobComplete(id), "Job factory thinks task already exited while it is active.");

            // terminate process
            remoteFunctions.StopJob(id);
            Assert.IsTrue(procList.First().HasExited, "Notepad is still running, even after terminating the job.");
        }

        [TestMethod]
        public void Job_CheckOutput()
        {
            var id = remoteFunctions.StartJob("systeminfo.exe");

            Assert.IsTrue(!remoteFunctions.IsJobComplete(id), "Job marked as exitted before it could have reasonably completed.");

            Stopwatch sw = new Stopwatch();
            sw.Start();

            while (sw.ElapsedMilliseconds < 10 * 1000)
            {
                if (remoteFunctions.IsJobComplete(id)) { break; }
                Task.Delay(1000).Wait();
            }
            sw.Stop();

            Assert.IsTrue(remoteFunctions.IsJobComplete(id), "Job did not finish within reasonable time.");

            var result = remoteFunctions.GetJobResult(id);
            Assert.IsTrue(result.Length > 0, "Length from command is 0 characters - this is incorrect.");
            Assert.IsTrue(result.Contains("OS Name:"), "Result doesn't contain expected items.");
        }

        [TestMethod]
        public void Job_CheckCallback()
        {
            var self = IPAddress.Loopback;
            var port = 13000;
            string receivedMessage = null;

            var id = remoteFunctions.StartJobWithNotification(self.ToString(), port, "systeminfo.exe");

            var server = new TcpListener(self, port);
            server.Start(1);
            var client = server.AcceptTcpClientAsync();

            if (!client.Wait(10 * 1000))
            {
                server.Stop();
                Assert.Fail("Timed out waiting for callback.");
            }

            using (var reader = new StreamReader(client.Result.GetStream()))
            {
                receivedMessage = reader.ReadToEnd();
            }
            client.Result.Dispose();

            Assert.IsTrue(receivedMessage == $"JOB {id} COMPLETED", "Callback has wrong data");
            Assert.IsTrue(remoteFunctions.IsJobComplete(id));
            
        }

        [TestMethod]
        public void Job_CheckCallback_AutodetectAddress_ExpectFail()
        {
            var self = IPAddress.Loopback;
            var port = 13000;
            string receivedMessage = null;

            Assert.ThrowsException<InvalidOperationException>(() => remoteFunctions.StartJobWithNotification(null, port, "systeminfo.exe"));
        }

        [TestMethod]
        public void Job_CheckCallback_AutodetectAddress_ExpectSuccess()
        {
            var port = 14000;
            string receivedMessage = null;

            // code to listen for callback
            var server = new TcpListener(IPAddress.Any, port);
            server.Start(1);
            var callbackClient = server.AcceptTcpClientAsync();

            // start up the server for this specific test
            var rpcserver = new SimpleJsonRpc.SimpleRpcServer();
            var rpcFunctions = new SimpleDUTRemote.Functions();
            rpcserver.Register(rpcFunctions);
            var rpcserverTask = rpcserver.Start();

            string output;
            var client = new TcpClient();
            client.ConnectAsync("localhost", 8000).Wait();

            using (client)
            using (var rstream = new StreamReader(client.GetStream()))
            using (var wstream = new StreamWriter(client.GetStream()))
            {
                var request = new JsonRpcRequest();
                request.method = "StartJobWithNotification";
                request.args = new List<object>() { null, port, "systeminfo.exe" };
                wstream.WriteLine(JsonConvert.SerializeObject(request) + "\r\n");
                wstream.Flush();

                output = rstream.ReadToEnd();
            }

            JObject resp = JObject.Parse(output);
            int id = (int) resp["result"].ToObject<long>();


            if (!callbackClient.Wait(10 * 1000))
            {
                server.Stop();
                Assert.Fail("Timed out waiting for callback.");
            }

            using (var reader = new StreamReader(callbackClient.Result.GetStream()))
            {
                receivedMessage = reader.ReadToEnd();
            }
            callbackClient.Result.Dispose();

            Assert.IsTrue(receivedMessage == $"JOB {id} COMPLETED", "Callback has wrong data");

        }



        [TestMethod]
        public void JobFactory_TestCallbackNoListener()
        {
            var self = IPAddress.Loopback;
            var port = 13000;
            string receivedMessage = null;

            // but don't create a listener - the connect method should time out, and continue

            var id = remoteFunctions.StartJobWithNotification(self.ToString(), port, "systeminfo.exe");

            Stopwatch sw = new Stopwatch();
            sw.Start();

            while (sw.ElapsedMilliseconds < 10 * 1000)
            {
                if (remoteFunctions.IsJobComplete(id)) { break; }
                Task.Delay(1000).Wait();
            }
            sw.Stop();

            Assert.IsTrue(remoteFunctions.IsJobComplete(id));

            var result = remoteFunctions.GetJobResult(id);

            Assert.IsTrue(result.Length > 0, "Length of output from command is 0");
            Assert.IsTrue(result.Contains("OS Name:"), "Result doesn't contain expected items.");
        }

        [TestMethod]
        public void Job_GetAllJobs()
        {
            var notepadId = remoteFunctions.StartJob("notepad.exe");
            var sysinfoId = remoteFunctions.StartJob("systeminfo.exe");

            while (!remoteFunctions.IsJobComplete(sysinfoId))
            {
                Task.Delay(1000).Wait();
            }

            // sysinfo should now be done, notepad should still be up.
            var currentJobs = remoteFunctions.GetAllJobs();

            Assert.IsTrue(currentJobs.ContainsKey(notepadId), "Missing notepad job id.");
            Assert.IsTrue(currentJobs.ContainsKey(sysinfoId), "Midding system info job id.");

            Assert.IsTrue(currentJobs[notepadId] == false, "Notepad marked as ended, even through it should still be running.");
            Assert.IsTrue(currentJobs[sysinfoId] == true, "System Info marked as running, even through it should have completed.");

            remoteFunctions.StopJob(notepadId);


        }


    }
}
