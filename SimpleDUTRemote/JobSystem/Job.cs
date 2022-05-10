﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using NLog;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SimpleDUTCommonLibrary;

namespace SimpleDUTRemote.JobSystem
{
    public class Job : IDisposable
    {
        public int jobId { get; private set; }
        private Process process;
        private static Logger logger = LogManager.GetLogger("JobSystem");
        private JobCallbackInfo callbackInfo;
        private static int nextJobId = 0;

        // used to capture non-streaming output in memory
        private HelperFunctions.ThreadSafeStringBuilder output = null;

        // items for progress streaming (network)
        private StreamWriter progressStream;
        private const int NETWORK_TIMEOUT_MS = 5000;
        private Task streamingLoopTask;
        private BlockingCollection<string> streamingCollection;

        // while streaming to network, we also stream to a backup file
        private const string OUTPUT_LOG_BASENAME = "SimpleRemote-JobOutput-";
        private const string OUTPUT_LOG_TIME_FORMAT = "yyyy-MM-dd_HH-mm-ss";
        private string outputLogFilename = null;
        private StreamWriter outputLogSteam = null;

        // class-level lock object to ensure stream cleanup is threadsafe.
        private object lockObj = new object();

        public static Job CreateJob(Process p, JobCallbackInfo callback = null)
        {
            var newid = Interlocked.Increment(ref nextJobId);
            return new Job(newid, p, callback);
        }

        public Job(int id, Process process, JobCallbackInfo callback = null)
        {
            logger.Info("Spawning new job id {0}; will call {1}", id, process.StartInfo.FileName);
            jobId = id;
            this.process = process;

            // if a progress port was specified in callback info, prepare for streaming
            if (callback != null && callback.ProgressPort > 0)
            {
                var streamEp = new IPEndPoint(callback.Address, callback.ProgressPort);
                CreateProgressStream(streamEp);

                streamingCollection = new BlockingCollection<string>();

                process.OutputDataReceived += (s, a) => streamingCollection.Add(a.Data);
                process.ErrorDataReceived += (s, a) => streamingCollection.Add(a.Data);

                streamingLoopTask = Task.Factory.StartNew(StreamingLoopHandler);
            }
            else
            {
                output = new HelperFunctions.ThreadSafeStringBuilder();
                process.OutputDataReceived += (s, a) => output.AppendLine(a.Data);
                process.ErrorDataReceived += (s, a) => output.AppendLine(a.Data);

            }

            // always log output
            process.OutputDataReceived += (s,a) => logger.Debug($"Job {this.jobId} std output: {a.Data}");
            process.ErrorDataReceived += (s,a) => logger.Debug($"Job {this.jobId} std error: {a.Data}");

            // add a logging message when a job finishes and ensure that streaming task stops if needed
            process.EnableRaisingEvents = true;
            process.Exited += (o, e) => logger.Info($"Job {id} finished executing.");
            process.Exited += (o, e) => streamingCollection?.Add(null);

            if (callback != null)
            {
                logger.Info("Registering job {0:d} for callbacks", id);

                // if callbacks are specified, we need to register for process events, and setup a handler
                callbackInfo = callback;
                
                // fire the callback handler in another thread (otherwise the callback might take a while and block other events waiting on exit)
                process.Exited += (o, e) => Task.Factory.StartNew(FireCompletionCallback);
            }

            process.Start();

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }

        private void StreamingLoopHandler()
        {
            string nextLine;

            while ((nextLine = streamingCollection.Take()) != null)
            {
                try
                {
                    outputLogSteam.WriteLine(nextLine);
                    progressStream?.WriteLine(nextLine);
                }
                catch (IOException e)
                {
                    if (!(e.InnerException is SocketException)) throw;

                    logger.Error("Failed to stream progress from process - socket exception ocurred.");
                    logger.Error("Logging will continue to the the file log.");
                    progressStream.Dispose();
                    progressStream = null;
                }
                catch (ObjectDisposedException)
                {
                    logger.Debug("Stream object was disposed while streaming output - this likely means this job was terminated.");
                    return; // break out of the function if this happens. 
                }
            }

            CloseStreams();

        }

        private void FireCompletionCallback()
        {
            logger.Debug("Attmpeting to send TCP completion message for job {0}", this.jobId);

            // if the streaming system is active, don't send this until the streams have completed
            if (streamingLoopTask != null && !streamingLoopTask.IsCompleted)
            {
                streamingLoopTask.Wait();
            }

            // Retry connection attempt with exponential backoff
            var retryAttempt = 0;
            bool clientConnected = false;

            while (retryAttempt < 5 && !clientConnected)
            {
                logger.Warn("Attmpeting to send TCP completion message for job {0}: Retry Attempt {1}", this.jobId, retryAttempt);
                using (var client = new TcpClient())
                {
                    // Delay and retry if the connection failed or network timeout was reached
                    if (!client.ConnectWithTimeout(callbackInfo.Address, callbackInfo.Port, NETWORK_TIMEOUT_MS))
                        {
                        // Backoff the retry delay time
                        var newDelay = Math.Pow(2, retryAttempt);
                        logger.Warn("Timeout or Task Fault, task states:\n\tConnection: Timed out\n\tTimeout: {0}\nRetry in: {1} seconds", NETWORK_TIMEOUT_MS, newDelay.ToString());

                        // Delay then retry connection
                        Thread.Sleep(TimeSpan.FromSeconds(newDelay));
                        retryAttempt++;     
                    }
                    else // Use the TCP Client and complete callback
                    {
                        logger.Debug("Task Completed, task states:\n\tConnection: OK\n\tTimeout: {0}", NETWORK_TIMEOUT_MS);
                        clientConnected = true;

                        using (var streamWriter = new StreamWriter(client.GetStream(), Encoding.ASCII))
                        {
                            streamWriter.Write("JOB {0:d} COMPLETED", jobId);
                        }
                    }
                }
            }

            // Log connection success or failure
            if (clientConnected)
            {
                logger.Debug("Successfully sent job completion message for job {0}", jobId);
            }
            else
            {
                logger.Warn("Failed to contact client with completion message for job {0}", jobId);
            }
        }

        public bool IsDone()
        {
            return process.HasExited;
        }

        public string GetResult()
        {
            if (!this.IsDone())
            {
                throw new InvalidOperationException("Process hasn't finished executing. Cannot get result.");
            }
            // wait until all streams have flushed
            // this call cannot have a timeout (see reference source - adding a timeout will cause streams
            // to be ignored), but we assume we're just waiting on logging, so any wait should be fast.
            logger.Debug($"Waiting for job {jobId} process output stream to flush");
            process.WaitForExit();
            logger.Debug($"Child process streams flushed on job {jobId}.");

            return output != null ? output.ToString() : String.Empty;
        }

        public int GetExitCode()
        {
            if (!this.IsDone())
            {
                throw new InvalidOperationException("Process hasn't finished executing. Cannot get exit code.");
            }
            return process.ExitCode;
        }

        public void WaitForCompletion()
        {
            if (!this.IsDone())
            {
                process.WaitForExit();
            }
        }

        public void Kill()
        {
            logger.Info("Terminating job id: {0}", jobId);
            process.Kill();
        }

        // connect to the client and create a network stream and create a filestream
        // to the file log.
        private bool CreateProgressStream(IPEndPoint streamEp)
        {

            bool connectionSuccessful = false;

            // setup file streaming regardless of what happens on the network;
            outputLogFilename = OUTPUT_LOG_BASENAME + DateTime.Now.ToString(OUTPUT_LOG_TIME_FORMAT) + ".txt";
            outputLogFilename = Path.Combine(Path.GetTempPath(), outputLogFilename);
            outputLogSteam = new StreamWriter(new FileStream(outputLogFilename, FileMode.Create));
            logger.Info("Recording process output to: {0} .", outputLogFilename);

            // make a note on the logfile what job this is and what was called.
            outputLogSteam.WriteLine($"SimpleRemote Job {jobId} Output - {DateTime.Now:g}");
            outputLogSteam.WriteLine($"{process.StartInfo.FileName} {process.StartInfo.Arguments}");
            outputLogSteam.WriteLine();

            var progressClient = new TcpClient();
            progressClient.SendTimeout = NETWORK_TIMEOUT_MS;

            try
            {
                if (!progressClient.ConnectWithTimeout(streamEp.Address, streamEp.Port, NETWORK_TIMEOUT_MS))
                {
                    // failed to connect due to timeout - log and proceed.
                    connectionSuccessful = false;
                    logger.Error("Failed to initiate network streaming progress - connection to client timed out ");
                    
                }
                else
                {
                    // connection successful
                    progressStream = new StreamWriter(progressClient.GetStream());
                    connectionSuccessful = true;
                }
            }
            catch (Exception e)
            {
                if (e is SocketException || (e is AggregateException && e.InnerException is SocketException))
                {
                    connectionSuccessful = false;
                    logger.Error("Failed to initiate network streaming progress - got socket exception while connecting");
                }
                else throw;
            }

            return connectionSuccessful;
        }

        private void CloseStreams()
        {
            // ensure cleanup is threadsafe
            lock (lockObj)
            {
                try
                {
                    // if necessary, shutdown network progress stream
                    if (progressStream != null)
                    {
                        progressStream.Close(); //closes network streams
                        progressStream = null;
                    }
                }
                catch (IOException e)
                {
                    if (!(e.InnerException is SocketException)) throw;
                    logger.Error("A SocketException occurred while closing progress socket streams.");

                }
                finally
                {
                    if (outputLogSteam != null)
                    {
                        outputLogSteam.Close(); //close file stream
                        outputLogSteam = null;
                    }
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool safeToCleanManagedResources)
        {
            if (safeToCleanManagedResources)
            {
                CloseStreams();
                this.process.Dispose();
            }
        }

        ~Job()
        {
            Dispose(false);
        }
    }

    public class JobCallbackInfo
    {
        public int Port;
        public IPAddress Address;

        public int ProgressPort;
    }
}
