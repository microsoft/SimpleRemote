using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using System.Threading;
using System.Net.Sockets;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using SimpleDUTRemote;
using System.Diagnostics;
using System.Linq;
using SimpleJsonRpc;

namespace DUTRemoteTests
{
    [TestClass]
    public class BasicRunTests
    {
        private SimpleRpcServer server;
        private Task serverTask;

        public BasicRunTests()
        {
            server = new SimpleRpcServer();
            var rpcFunctions = new SimpleDUTRemote.Functions();
            server.Register(rpcFunctions);
            serverTask = server.Start();
        }

        [TestMethod]
        public void RunWithoutResult()
        {
            string output;
            using (var client = GetClient())
            using (var rstream = new StreamReader(client.GetStream()))
            using (var wstream = new StreamWriter(client.GetStream()))
            {
                var request = new JsonRpcRequest();
                request.method = "Run";
                request.args = new List<object>() { @"C:\Program Files\Internet Explorer\iexplore.exe" };
                wstream.WriteLine(JsonConvert.SerializeObject(request) + "\r\n");
                wstream.Flush();

                output = rstream.ReadToEnd();
            }

            var resp = JsonConvert.DeserializeObject<JsonRpcResponse>(output);

            Assert.IsNull(resp.error);
            Assert.IsTrue((bool) resp.result == true);

            // confirm IE started
            var procList = Process.GetProcessesByName("iexplore");
            Assert.IsTrue(procList.Length > 0, "No IE instance found.");
            Assert.IsTrue(procList.First().HasExited == false);

            // terminate it
            procList.First().Kill();
        }

        [TestMethod]
        public void RunWithResult()
        {
            string output;
            using (var client = GetClient())
            using (var rstream = new StreamReader(client.GetStream()))
            using (var wstream = new StreamWriter(client.GetStream()))
            {
                var request = new JsonRpcRequest();
                request.method = "RunWithResult";
                request.args = new List<object>() { "systeminfo.exe" };
                wstream.WriteLine(JsonConvert.SerializeObject(request) + "\r\n");
                wstream.Flush();

                output = rstream.ReadToEnd();
            }

            var resp = JsonConvert.DeserializeObject<JsonRpcResponse>(output);

            Assert.IsNull(resp.error);
            Assert.IsTrue(((string)resp.result).Length > 0, "Length of output from command is 0");
            Assert.IsTrue(((string)resp.result).Contains("OS Name:"), "Result doesn't contain expected items.");
        }

        [TestMethod]
        public void KillProcess()
        {
            // start our process
            var p = Process.Start(@"C:\Program Files\Internet Explorer\iexplore.exe");

            // confirm it's actually started
            Assert.IsFalse(p.HasExited);

            string output;
            using (var client = GetClient())
            using (var rstream = new StreamReader(client.GetStream()))
            using (var wstream = new StreamWriter(client.GetStream()))
            {
                var request = new JsonRpcRequest();
                request.method = "KillProcess";
                request.args = new List<object>() { "iexplore" };
                wstream.WriteLine(JsonConvert.SerializeObject(request) + "\r\n");
                wstream.Flush();

                output = rstream.ReadToEnd();
            }

            var resp = JsonConvert.DeserializeObject<JsonRpcResponse>(output);

            Assert.IsNull(resp.error);
            Assert.IsTrue((bool) resp.result == true);

            // confirm IE terminated
            Assert.IsTrue(p.HasExited);
        }

        [TestMethod]
        public void RpcServer_StopServer()
        {
            var altServer = new SimpleRpcServer();
            var rpcFunctions = new SimpleDUTRemote.Functions();
            altServer.Register(rpcFunctions);
            var serverTask = altServer.Start(9000);
            Thread.Sleep(500);
            altServer.Stop();

            var finished = serverTask.Wait(1000);

            Assert.IsTrue(finished, "Server did not stop.");
        }


        private TcpClient GetClient()
        {
            // return a connection to the current server
            var client = new TcpClient();
            client.ConnectAsync("localhost", 8000).Wait();

            return client;
        }
    }
}
