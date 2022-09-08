﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimpleDUTClientLibrary;
using SimpleDUTRemote;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.IO;
using System.Net.Sockets;
using SimpleJsonRpc;
using System.Security.Principal;
using Newtonsoft.Json.Linq;

namespace DUTRemoteTests
{
    [TestClass]
    public class ClientTests
    {
        private SimpleRpcServer server;
        private Task serverTask;
        private RpcClient client;

        public ClientTests()
        {
            server = new SimpleRpcServer();
            var rpcFunctions = new SimpleDUTRemote.Functions();
            server.Register(rpcFunctions);
            serverTask = server.Start();
            client = new RpcClient("127.0.0.1", 8000);

            // setup nlog tracking (off by default)
            // use this when debugging since vstest doesn't capture output.
            /*
            var config = new NLog.Config.LoggingConfiguration();
            config.AddRuleForAllLevels(new NLog.Targets.FileTarget("logfile")
            {
                FileName = "client_test_log.txt"
            });

            NLog.LogManager.Configuration = config;
            */
        }

        [TestMethod]
        public void Client_RunAndKillProcess()
        {
            // test run

            bool status = client.Run(@"notepad.exe");
            Assert.IsTrue(status);

            var sw = new Stopwatch();
            sw.Start();

            Process[] procList = new Process[0];
            status = CheckIfTrueInTimelimit(() =>
            {
               procList = Process.GetProcessesByName("notepad");
               return (procList.Length > 0);
            }, 5000);

            Assert.IsTrue(status, "Did not spawn process within time limit.");

            // test process kill
            status = client.KillProcess("notepad");
            Assert.IsTrue(status);

            status = CheckIfTrueInTimelimit(() =>
            {
                procList = Process.GetProcessesByName("notepad");
                return (procList.Length < 1);
            }, 5000);

            Assert.IsTrue(status, "Process did not exit.");
        }

        [TestMethod]
        public void Client_RunProcessWithOutput()
        {
            string result = client.RunJob("systeminfo.exe");

            Assert.IsTrue(result.Length > 0);
            Assert.IsTrue(result.Contains("OS Name:"), "Result doesn't contain expected items.");
        }

        [TestMethod]
        public void Client_RunProcessWithOutput_WithTimeout()
        {
            // it should take longer than 1 second to complete, so we should get an aggregate exception
            var e = Assert.ThrowsException<AggregateException>(() => client.RunJob("systeminfo.exe", timeout: 1));

            Assert.IsInstanceOfType(e.InnerException, typeof(TimeoutException));
        }

        [TestMethod]
        public void Client_RunProcessWithArgs()
        {
            string result = client.RunJob("powershell.exe", "-Help" );

            Assert.IsTrue(result.Length > 0);
            Assert.IsTrue(result.Contains("Shows this message."), "Failed to call powershell with -Help argument.");
        }

        [TestMethod]
        public void Client_GetAllJobs()
        {
            var sysinfo = client.StartJob("systeminfo.exe");
            var notepad = client.StartJob("notepad.exe");

            while (!client.CheckJobCompletion(sysinfo))
            {
                Task.Delay(1000).Wait();
            }

            var jobs = client.GetAllJobs();

            Assert.IsTrue(jobs.ContainsKey(notepad), "Missing notepad job id.");
            Assert.IsTrue(jobs.ContainsKey(sysinfo), "Midding system info job id.");

            Assert.IsTrue(jobs[notepad] == false, "Notepad marked as ended, even through it should still be running.");
            Assert.IsTrue(jobs[sysinfo] == true, "System Info marked as running, even through it should have completed.");

            client.StopJob(notepad);
        }

        [TestMethod]
        public void Client_StartJobWithNotification_CheckJobOutput()
        {
            AutoResetEvent evt = new AutoResetEvent(false);
            int completedJob = -1;
            Action<int> cb = (jobid) =>
            {
                completedJob = jobid;
                evt.Set();
            };

            int newJobId = client.StartJobWithNotification("systeminfo.exe", null, cb);

            Assert.IsTrue(newJobId > 0);

            Assert.IsTrue(evt.WaitOne(5000), "Callback took too long to be received.");
            Assert.IsTrue(completedJob == newJobId);

            Assert.IsTrue(client.CheckJobCompletion(completedJob), "Job declared complete does not appear to be complete on server.");

            string result = client.GetJobResult(completedJob);
            Assert.IsTrue(result.Length > 0);
            Assert.IsTrue(result.Contains("OS Name:"), "Result doesn't contain expected items.");
        }

        [TestMethod]
        public void Client_StartJobWithProgress_CheckJobOutput()
        {
            AutoResetEvent evt = new AutoResetEvent(false);
            int completedJob = -1;
            var completedMsgs = new List<string>();

            Action<int> cb = (jobid) =>
            {
                completedJob = jobid;
                evt.Set();
            };

            Action<string> progressEvent = (msg) => completedMsgs.Add(msg);

            int newJobId = client.StartJobWithProgress("systeminfo.exe", null, progressEvent, cb);

            Assert.IsTrue(newJobId > 0);

            Assert.IsTrue(evt.WaitOne(5000), "Callback took too long to be received.");
            Assert.IsTrue(completedJob == newJobId);

            Assert.IsTrue(client.CheckJobCompletion(completedJob), "Job declared complete does not appear to be complete on server.");

            string result = client.GetJobResult(completedJob);
            Assert.IsTrue(result.Length == 0, "Result was greater than zero, even though we used StartJobWithProgress");
            
            Assert.IsTrue(completedMsgs.Count() > 0, "Did not receive any progress messages.");
            var OSNameMsg = completedMsgs.Where((x) => x.StartsWith("OS Name:"));
            Assert.IsTrue(OSNameMsg.Count() > 0, "Did not receive a message with OS Name");

        }

        [TestMethod]
        public void Client_StartJobWithArgsAndNotification()
        {
            AutoResetEvent evt = new AutoResetEvent(false);
            Action<int> cb = (jobid) =>
            {
                evt.Set();
            };

            int newJobId = client.StartJobWithNotification("powershell.exe", "-Help", cb);
            Assert.IsTrue(evt.WaitOne(5000), "Callback took too long to be received.");

            string result = client.GetJobResult(newJobId);

            Assert.IsTrue(result.Contains("Shows this message."), "Failed to start job with powershell using -Help argument.");
        }

        [TestMethod]
        public void Client_RunWithResultAsync()
        {
            var task = client.RunJobAsync("systeminfo.exe", null);
            var completed = task.Wait(5000);

            Assert.IsTrue(completed, "RunWithResultAsync task did not complete in a timely manner.");
            Assert.IsTrue(task.IsCompleted && !task.IsFaulted, "RunWithResultAsync task failed to complete.");

            var result = task.Result;
            Assert.IsTrue(result.Length > 0);
            Assert.IsTrue(result.Contains("OS Name:"), "RunWithResultAsync output doesn't contain expected items.");
        }

        [TestMethod]
        public void Client_RunWithResultAsyncEx()
        {
            var task = client.RunJobAsyncEx("systeminfo.exe", null);
            var completed = task.Wait(5000);

            Assert.IsTrue(completed, "RunJobAsyncEx task did not complete in a timely manner.");
            Assert.IsTrue(task.IsCompleted && !task.IsFaulted, "RunJobAsyncEx task failed to complete.");

            var result = task.Result;
            var jObj = JObject.Parse(result);

            var output = (string) jObj["output"];
            var exitCode = (long) jObj["exitCode"];

            Assert.IsTrue(output.Length > 0);
            Assert.IsTrue(output.Contains("OS Name:"), "RunJobAsyncEx output doesn't contain expected items.");
            Assert.IsTrue(exitCode == 0, "RunJobAsyncEx called process didn't return exit code 0.");
        }

        [TestMethod]
        public void Client_RunWithResultAsync_TestCancellation()
        {
            var tokenSource = new CancellationTokenSource();
            var token = tokenSource.Token;

            var task = client.RunJobAsync("systeminfo.exe", cancellationToken: token);
            tokenSource.Cancel();

            string result;
            var e = Assert.ThrowsException<AggregateException>(() => result = task.Result);

            Assert.IsInstanceOfType(e.InnerException, typeof(OperationCanceledException));

            
        }

        [TestMethod]
        public void Client_Upload_UseFile_UseRelativeSendPath()
        {
            // determine file paths
            var fileToSend = "sampleFileToSend";
            var dirToRecv = Path.Combine(Path.GetTempPath(), "sampleRecvDir");
            var fileToRecv = Path.Combine(dirToRecv, "sampleFileToSend");

            Directory.CreateDirectory(dirToRecv);

            // create 100 MB random data, and write it to our file.
            byte[] data = new byte[100 * 1024 * 1024];
            Random rng = new Random();
            rng.NextBytes(data);
            File.WriteAllBytes(fileToSend, data);

            // perform the upload
            client.Upload(fileToSend, dirToRecv, true);

            Thread.Sleep(500); // give time for writes to disk to complete.
            byte[] receivedData = File.ReadAllBytes(fileToRecv);
            Assert.IsTrue(data.SequenceEqual(receivedData), "Data received and data sent do not match.");

            // if we get to this point, delete extra files
            File.Delete(fileToSend);
            File.Delete(fileToRecv);

            Directory.Delete(dirToRecv);
        }

        [TestMethod]
        public void Client_Upload_UseFile()
        {
            // determine file paths
            var fileToSend = Path.GetTempPath() + "sampleFileToSend";
            var dirToRecv = Path.Combine(Path.GetTempPath(), "sampleRecvDir");
            var fileToRecv = Path.Combine(dirToRecv, "sampleFileToSend");

            Directory.CreateDirectory(dirToRecv);

            // create 100 MB random data, and write it to our file.
            byte[] data = new byte[100 * 1024 * 1024];
            Random rng = new Random();
            rng.NextBytes(data);
            File.WriteAllBytes(fileToSend, data);

            // perform the upload
            client.Upload(fileToSend, dirToRecv, true);

            Thread.Sleep(500); // give time for writes to disk to complete.
            byte[] receivedData = File.ReadAllBytes(fileToRecv);
            Assert.IsTrue(data.SequenceEqual(receivedData), "Data received and data sent do not match.");

            // if we get to this point, delete extra files
            File.Delete(fileToSend);
            File.Delete(fileToRecv);

            Directory.Delete(dirToRecv);
        }

        [TestMethod]
        public void Client_Upload_UseFile_CheckOverwrite()
        {
            // determine file paths
            var fileToSend = Path.GetTempPath() + "sampleFileToSend";
            var dirToRecv = Path.Combine(Path.GetTempPath(), "sampleRecvDir");
            var fileToRecv = Path.Combine(dirToRecv, "sampleFileToSend");

            Directory.CreateDirectory(dirToRecv);

            // create 100 MB random data, and write it to our file.
            byte[] data = new byte[100 * 1024 * 1024];
            Random rng = new Random();
            rng.NextBytes(data);
            File.WriteAllBytes(fileToSend, data);

            // perform the upload
            client.Upload(fileToSend, dirToRecv, true);

            Thread.Sleep(500); // give time for writes to disk to complete.
            byte[] receivedData = File.ReadAllBytes(fileToRecv);
            Assert.IsTrue(data.SequenceEqual(receivedData), "Data received and data sent do not match.");

            // redo the upload with new data
            rng.NextBytes(data);
            File.WriteAllBytes(fileToSend, data);

            // upload again
            client.Upload(fileToSend, dirToRecv, true);

            Thread.Sleep(500); // give time for writes to disk to complete.
            receivedData = File.ReadAllBytes(fileToRecv);
            Assert.IsTrue(data.SequenceEqual(receivedData), "Data received (after overwrite) and data sent do not match.");

            // check the this fails if we try to upload again with overwrite off
            // and confirm existing file isn't altered
            var originalData = data;
            byte[] newdata = new byte[10 * 1024 * 1024];
            rng.NextBytes(newdata);
            File.WriteAllBytes(fileToSend, data);

            // attempt upload - this should fail
            try
            {
                client.Upload(fileToSend, dirToRecv, false);
                Assert.Fail("Upload succeeded even when target already existed and overwrite was false.");
            }
            catch (Exception ex)
            {
                // confirm we're not catching the assertion failed
                if (ex is AssertFailedException) throw;

                // otherwsie, we're fine. 
            }

            // confirm that the file on disk was not changed
            Assert.IsTrue(originalData.SequenceEqual(File.ReadAllBytes(fileToRecv)), "Existing file changed (even though overwrite was off).");

            // if we get to this point, delete extra files
            File.Delete(fileToSend);
            File.Delete(fileToRecv);

            Directory.Delete(dirToRecv);
        }

        [TestMethod]
        public void Client_Upload_UseDirectory()
        {
            var sampleRoot = PrepareDirectoryTests();
            var sendRoot = Path.Combine(sampleRoot, "sent");
            var recvRoot = Path.Combine(sampleRoot, "received");

            client.Upload(sendRoot, recvRoot, true);

            Thread.Sleep(1000);

            Assert.IsTrue(Directory.Exists(recvRoot), "Recieve root directory not created.");
            Assert.IsTrue(Directory.Exists(Path.Combine(recvRoot, "bar")), "Sub directory 'bar' not created.");

            Assert.IsTrue(File.ReadAllText(Path.Combine(recvRoot, "foo.txt")).Contains("the quick brown fox"), "Top level file is missing expected content.");
            Assert.IsTrue(File.ReadAllText(Path.Combine(recvRoot, "bar", "baz.txt")).Contains("he broke a new shoelace"), "Nested file is missing content.");
        }

        [TestMethod]
        public void Client_UploadFileAsync()
        {
            // determine file paths
            var fileToSend = Path.GetTempPath() + "sampleFileToSend";
            var dirToRecv = Path.Combine(Path.GetTempPath(), "sampleRecvDir");
            var fileToRecv = Path.Combine(dirToRecv, "sampleFileToSend");

            Directory.CreateDirectory(dirToRecv);

            // create 100 MB random data, and write it to our file.
            byte[] data = new byte[100 * 1024 * 1024];
            Random rng = new Random();
            rng.NextBytes(data);
            File.WriteAllBytes(fileToSend, data);

            // perform the upload
            var task = client.UploadAsync(fileToSend, dirToRecv, true);

            Assert.IsTrue(task.Wait(10000), "UploadFileAsync took longer than expected.");
            Assert.IsTrue(task.IsCompleted && !task.IsFaulted, "UploadFileAsync task failed to complete without error.");

            Thread.Sleep(500); // give time for writes to disk to complete.
            byte[] receivedData = File.ReadAllBytes(fileToRecv);
            Assert.IsTrue(data.SequenceEqual(receivedData), "Data received and data sent do not match.");

            // if we get to this point, delete extra files
            File.Delete(fileToSend);
            File.Delete(fileToRecv);

            Directory.Delete(dirToRecv);
        }

        [TestMethod]
        public void Client_DownloadFile()
        {
            // determine file paths
            var fileToSend = Path.GetTempPath() + "sampleFileToSend";
            var dirToRecv = Path.Combine(Path.GetTempPath(), "sampleRecvDir");
            var fileToRecv = Path.Combine(dirToRecv, "sampleFileToSend");

            Directory.CreateDirectory(dirToRecv);

            // create 100 MB random data, and write it to our file.
            byte[] data = new byte[100 * 1024 * 1024];
            Random rng = new Random();
            rng.NextBytes(data);
            File.WriteAllBytes(fileToSend, data);

            // perform the upload
            client.Download(fileToSend, dirToRecv, true);

            Thread.Sleep(500); // give time for writes to disk to complete.
            byte[] receivedData = File.ReadAllBytes(fileToRecv);
            Assert.IsTrue(data.SequenceEqual(receivedData), "Data received and data sent do not match.");

            // if we get to this point, delete extra files
            File.Delete(fileToSend);
            File.Delete(fileToRecv);

            Directory.Delete(dirToRecv);
        }

        [TestMethod]
        public void Client_DownloadFile_SpecifyPort()
        {

            // determine file paths
            var fileToSend = Path.GetTempPath() + "sampleFileToSend";
            var dirToRecv = Path.Combine(Path.GetTempPath(), "sampleRecvDir");
            var fileToRecv = Path.Combine(dirToRecv, "sampleFileToSend");

            Directory.CreateDirectory(dirToRecv);

            // create 100 MB random data, and write it to our file.
            byte[] data = new byte[100 * 1024 * 1024];
            Random rng = new Random();
            rng.NextBytes(data);
            File.WriteAllBytes(fileToSend, data);

            // perform the upload
            client.Download(fileToSend, dirToRecv, true, 9099);

            Thread.Sleep(500); // give time for writes to disk to complete.
            byte[] receivedData = File.ReadAllBytes(fileToRecv);
            Assert.IsTrue(data.SequenceEqual(receivedData), "Data received and data sent do not match.");

            // if we get to this point, delete extra files
            File.Delete(fileToSend);
            File.Delete(fileToRecv);

            Directory.Delete(dirToRecv);
        }

        [TestMethod]
        public void Client_GetVersion()
        {
            var version = client.GetVersion();

            Assert.IsTrue(version.Length > 0, "Version string isn't the right length");
            Assert.IsTrue(version.Where((a) => a == '.').Count() >= 2, "Version string doesn't have right format");
        }

        [TestMethod]
        public void Client_GetHeartbeat()
        {
            var res = client.GetHeartbeat();
            Assert.IsTrue(res, "Heartbeat failed to return true.");
        }

        [TestMethod]
        public void Client_GetVersionUsingHostname()
        {
            RpcClient myclient = new RpcClient("localhost", 8000);
            var version = client.GetVersion();

            Assert.IsTrue(version.Length > 0, "Version string isn't the right length");
            Assert.IsTrue(version.Where((a) => a == '.').Count() >= 2, "Version string doesn't have right format");
        }

        [TestMethod]
        public void Client_BadHostnameLookup()
        {
            RpcClient myclient;
            var exception = Assert.ThrowsException<SocketException>( () => myclient = new RpcClient("ThisIsAFakeHostName", 8000, 10000), 
                "System didn't fail when resolving an invalid hostname.");

        }

        [TestMethod]
        public void Client_BadEndpointConnection()
        {
            // there is no server on port 9000
            RpcClient myclient = new RpcClient("localhost", 9000);

            var exception = Assert.ThrowsException<Exception>(() => myclient.GetVersion(),
                "System didn't fail when connecting to an invalid endpoint.");
        }

        [TestMethod]
        public void Client_UploadDirectory()
        {
            var sampleRoot = PrepareDirectoryTests();
            var sendRoot = Path.Combine(sampleRoot, "sent");
            var recvRoot = Path.Combine(sampleRoot, "received");
            Directory.CreateDirectory(recvRoot);

            client.Upload(sendRoot, recvRoot, true);

            Thread.Sleep(1000);

            //Assert.IsTrue(Directory.Exists(recvRoot), "Recieve root directory not created."); //test is only valid if we generate the top level folder (we don't right now)
            Assert.IsTrue(Directory.Exists(Path.Combine(recvRoot, "bar")), "Sub directory 'bar' not created.");

            Assert.IsTrue(File.ReadAllText(Path.Combine(recvRoot, "foo.txt")).Contains("the quick brown fox"), "Top level file is missing expected content.");
            Assert.IsTrue(File.ReadAllText(Path.Combine(recvRoot, "bar", "baz.txt")).Contains("he broke a new shoelace"), "Nested file is missing content.");
        }

        [TestMethod]
        public void Client_Download_UseDirectory()
        {
            var sampleRoot = PrepareDirectoryTests();
            var sendRoot = Path.Combine(sampleRoot, "sent");
            var recvRoot = Path.Combine(sampleRoot, "received");
            Directory.CreateDirectory(recvRoot);

            client.Download(sendRoot, recvRoot, true);

            Thread.Sleep(1000);

            //Assert.IsTrue(Directory.Exists(recvRoot), "Recieve root directory not created."); //test is only valid if we generate the top level folder (we don't right now)
            Assert.IsTrue(Directory.Exists(Path.Combine(recvRoot, "bar")), "Sub directory 'bar' not created.");

            Assert.IsTrue(File.ReadAllText(Path.Combine(recvRoot, "foo.txt")).Contains("the quick brown fox"), "Top level file is missing expected content.");
            Assert.IsTrue(File.ReadAllText(Path.Combine(recvRoot, "bar", "baz.txt")).Contains("he broke a new shoelace"), "Nested file is missing content.");
        }

        [TestMethod]
        public void Client_Download_DownloadFileWithGlobExp()
        {
            // determine file paths
            var fileToSend = Path.GetTempPath() + "sampleFileToSend";
            var dirToRecv = Path.Combine(Path.GetTempPath(), "sampleRecvDir");
            var fileToRecv = Path.Combine(dirToRecv, "sampleFileToSend");

            Directory.CreateDirectory(dirToRecv);

            // create 100 MB random data, and write it to our file.
            byte[] data = new byte[100 * 1024 * 1024];
            Random rng = new Random();
            rng.NextBytes(data);
            File.WriteAllBytes(fileToSend, data);

            // perform the upload
            client.Download(Path.GetTempPath() + "sampleFileTo*", dirToRecv, true);

            Thread.Sleep(500); // give time for writes to disk to complete.
            byte[] receivedData = File.ReadAllBytes(fileToRecv);
            Assert.IsTrue(data.SequenceEqual(receivedData), "Data received and data sent do not match.");

            // if we get to this point, delete extra files
            File.Delete(fileToSend);
            File.Delete(fileToRecv);

            Directory.Delete(dirToRecv);
        }

        [TestMethod]
        public void Client_Download_DownloadMultipleWithGlobExp()
        {
            var sampleRoot = PrepareDirectoryTests();
            var sendRoot = Path.Combine(sampleRoot, "sent");
            var recvRoot = Path.Combine(sampleRoot, "received");
            Directory.CreateDirectory(recvRoot);

            // this gives us send/foo.txt, send/bar, and send/bar/baz.txt
            // we'll now create send/bat.txt and try to receive send/ba*
            File.WriteAllText(Path.Combine(sendRoot, "bat.txt"), "I am a file named bat.txt");

            // try to download all of send/ba, which should include everything in bar and bat.txt
            // but not foo
            var bytesFetched = client.Download(Path.Combine(sendRoot, "ba*"), recvRoot, true);

            // confirm we received bar and bat, not foo
            Assert.IsTrue(File.Exists(Path.Combine(recvRoot, "bat.txt")));
            Assert.IsTrue(Directory.Exists(Path.Combine(recvRoot, "bar")));
            Assert.IsFalse(File.Exists(Path.Combine(recvRoot, "foo.txt")));

            // confirm we received bar/baz
            Assert.IsTrue(File.Exists(Path.Combine(recvRoot, "bar", "baz.txt")));

            // confirm sizes of what was sent vs received
            var totalBytes = Directory.GetFiles(recvRoot, "*", SearchOption.AllDirectories)
                .Select(x => (new FileInfo(x).Length))
                .Sum();
            Assert.IsTrue(bytesFetched == totalBytes);
        }

        [TestMethod]
        [DeploymentItem(@"PluginExample.dll")]
        public void Client_LoadAndRunExtension()
        {
            client.PluginLoad("testId", "PluginExample.SimpleTest", "PluginExample.dll");
            var res = client.PluginCallMethod<string>("testId", "SayHiToMe", "FOO");
            Assert.IsTrue(res == "Hello FOO", "Result string was not correct.");
        }

        [TestMethod]
        [DeploymentItem(@"PluginExample.dll")]
        public void Client_LoadAndRunExtensionWithNullReturn()
        {
            client.PluginLoad("testId", "PluginExample.SimpleTest", "PluginExample.dll");
            var res = client.PluginCallMethod<object>("testId", "WriteToConsole");
            Assert.IsNull(res, "Function returned a value, even though the function should have returned null");
        }

        [TestMethod]
        [DeploymentItem(@"PluginExample.dll")]
        public void Client_InvalidExtensionLoad()
        {
            var ex = Assert.ThrowsException<Exception>(() => client.PluginLoad("testId", "PluginExample.SimpleTest", "Not_A_Path.dll"));
            Assert.IsTrue(ex.Message.Contains("IOException"), "The exception from the server didn't have the expected IOException in the message.");
        }

        [TestMethod]
        public void Client_IsServerRunningAsAdmin()
        {
            var isTestRunningAsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            Assert.AreEqual(isTestRunningAsAdmin, client.GetIsRunningAsAdmin(), "Server did not return a correct response for if it was running as admin.");
        }

        [TestMethod]
        public void Client_GetServerPath()
        {
            var thisAssemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var reportedLocation = client.GetServerLocation();

            Assert.AreEqual(Path.GetDirectoryName(reportedLocation), Path.GetDirectoryName(thisAssemblyLocation), 
                "Entry assembly locations don't match.");
        }

        [TestMethod]
        public void Client_RunProcessAndKill_ExpectCompletionCallback()
        {
            // for this, we need a job that will last several seconds, and generate output.
            // so we're going to send a powershell script that just prints 0 to 4, each
            // with a one second gap.
            // Powershell:  for ($i=0; $i -lt 5; $i++) { Write-Host "Message $i"; Start-Sleep -Seconds 1 }
            // Base64 Script (UTF16LE): ZgBvAHIAIAAoACQAaQA9ADAAOwAgACQAaQAgAC0AbAB0ACAANQA7ACAAJABpACsAKwApACAAewAgAFcAcgBpAHQAZQAtAEgAbwBzAHQAIAAiAE0AZQBzAHMAYQBnAGUAIAAkAGkAIgA7ACAAUwB0AGEAcgB0AC0AUwBsAGUAZQBwACAALQBTAGUAYwBvAG4AZABzACAAMQAgAH0A

            var powershellScript = "ZgBvAHIAIAAoACQAaQA9ADAAOwAgACQAaQAgAC0AbAB0ACAANQA7ACAAJABpACsAKwApACAAewAgAFcAcgBpAHQAZQAtAEgAbwBzAHQAIAAiAE0AZQBzAHMAYQBnAGUAIAAkAGkAIgA7ACAAUwB0AGEAcgB0AC0AUwBsAGUAZQBwACAALQBTAGUAYwBvAG4AZABzACAAMQAgAH0A";

            AutoResetEvent evt = new AutoResetEvent(false);
            Action<int> cb = (jobid) =>
            {
                evt.Set();
            };

            int newJobId = client.StartJobWithNotification("powershell.exe", $"-EncodedCommand {powershellScript}", cb);
            Thread.Sleep(2000);
            var killJobStatus = client.StopJob(newJobId);
            Assert.IsTrue(killJobStatus, "Failed to kill remote job.");
            Assert.IsTrue(evt.WaitOne(10000), "Completion callback never received.");
        }


        private bool CheckIfTrueInTimelimit(Func<bool> test, int timeout = 5000)
        {
            var sw = new Stopwatch();
            sw.Start();

            do
            {
                if (test()) return true;
            } while (sw.ElapsedMilliseconds < timeout);

            return false;

        }

        public static string PrepareDirectoryTests()
        {
            var sampleBasePath = Path.Combine(Path.GetTempPath(), "TestDirUpDown");
            var sendPath = Path.Combine(sampleBasePath, "sent");

            if (Directory.Exists(sampleBasePath)) { (new DirectoryInfo(sampleBasePath)).Delete(true); }
            Directory.CreateDirectory(sendPath);
            Directory.CreateDirectory(Path.Combine(sendPath, "bar")); //nested directory for testing

            File.WriteAllText(Path.Combine(sendPath, "foo.txt"), "the quick brown fox jumped over the stream.");
            File.WriteAllText(Path.Combine(sendPath, "bar", "baz.txt"), "he broke a new shoelace that day");

            return sampleBasePath;
        }
    }
}
