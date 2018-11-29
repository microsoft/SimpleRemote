// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDUTRemote.JobSystem
{
    public class Job : IDisposable
    {
        public int jobId { get; private set; }
        private Process process;
        private StringBuilder output;
        private static Logger logger = LogManager.GetLogger("JobSystem");
        private JobCallbackInfo callbackInfo;
        private static int nextJobId = 0;

        //items specifically for progress streaming
        private TcpClient progressClient;
        private StreamWriter progressStream;
        private const int NETWORK_TIMEOUT_MS = 5000;

        // items for falling back to a log if progress streaming fails.
        private const string EMERGENCY_LOG_BASENAME = "SimpleRemote-JobOutput-";
        private const string EMERGENCY_LOG_TIME_FORMAT = "s";
        private string emergencyLogFilename = null;
        private StreamWriter emergencyLogStream = null;

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
                emergencyLogFilename = EMERGENCY_LOG_BASENAME + DateTime.Now.ToString(EMERGENCY_LOG_TIME_FORMAT) + ".txt";
                var streamEp = new IPEndPoint(callback.Address, callback.ProgressPort);
                progressClient = new TcpClient();
                progressClient.SendTimeout = NETWORK_TIMEOUT_MS;
                if (!progressClient.ConnectAsync(streamEp.Address,streamEp.Port).Wait(NETWORK_TIMEOUT_MS))
                {
                    // failed to connect - log and proceed.
                    logger.Warn("Failed to initiate streaming progress - could not connect to client ");
                    logger.Warn("Using file log {0} instead.", emergencyLogFilename);
                    emergencyLogStream = new StreamWriter(new FileStream(emergencyLogFilename, FileMode.Create));
                }
                else
                {
                    // connection successful
                    progressStream = new StreamWriter(progressClient.GetStream());
                }
                    
            }

            output = new StringBuilder();

            DataReceivedEventHandler outputHandler = (s, a) =>
            {
                logger.Debug($"Job {id} output: {a.Data}");

                try{
                    // try to log to the emergency stream first, because if it exists, 
                    // that means we tried to stream and failed
                    if (emergencyLogStream != null)
                    {
                        emergencyLogStream.WriteLine(a.Data);
                    }
                    // if there's no emergency stream, try to use the progress stream if it's there
                    else if (progressStream != null)
                    {
                        progressStream.WriteLine(a.Data);
                    }
                    // if there's nothing else, use the internal buffer, because we weren't streaming
                    else {
                        output.AppendLine(a.Data);
                    }
                }
                catch (SocketException)
                {
                    logger.Warn("Failed to stream progress from process - socket exception ocurred.");
                    logger.Warn("Switching file log {0} instead.", emergencyLogFilename);

                    // a write to a socket failed. switch to the emergency file log.
                    emergencyLogStream = new StreamWriter(new FileStream(emergencyLogFilename, FileMode.Create));

                    // write the data
                    emergencyLogStream.WriteLine(a.Data);

                    // close the socket and network stream
                    progressStream.Close();
                    progressClient.Close();
                    progressStream = null;
                    progressClient = null;
                }
            };

            process.OutputDataReceived += outputHandler;
            process.ErrorDataReceived += outputHandler;

            // add a logging message when a job finishes and ensure that streams are closed (if present)
            process.EnableRaisingEvents = true;
            process.Exited += (o, e) => logger.Info($"Job {id} finished executing.");
            process.Exited += (o, e) => CloseStreams();

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

        private void FireCompletionCallback()
        {
            logger.Debug("Attmpeting to send TCP completion message for job {0}", this.jobId);

            using (var client = new TcpClient())
            {
                // TCP Client's Connect method doesn't have a settable timeout, but we can cheat using this method.
                // http://stackoverflow.com/questions/17118632/how-to-set-the-timeout-for-a-tcpclient
                if (!client.ConnectAsync(callbackInfo.Address, callbackInfo.Port).Wait(NETWORK_TIMEOUT_MS))
                {
                    logger.Warn("Failed to contact client with completion message for job {0}", jobId);
                    return;
                }

                using (var streamWriter = new StreamWriter(client.GetStream(), Encoding.ASCII))
                {
                    streamWriter.Write("JOB {0:d} COMPLETED", jobId);
                }
            }

            logger.Debug("Successfully sent job completion message for job {0}", jobId);
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
            return output.ToString();
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
            Dispose();
        }

        private void CloseStreams()
        {
            // if necessary, shutdown progress stream or emergency stream
            if (progressStream != null)
            {
                progressStream?.Close(); //closes network streams
                progressClient?.Close(); //closes tcp client.
                progressStream = null;
                progressClient = null;
            }
            if (emergencyLogStream != null)
            {
                emergencyLogStream.Close(); //close file stream
                emergencyLogStream = null;
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
