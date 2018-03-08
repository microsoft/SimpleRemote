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
    public class Job
    {
        public int jobId { get; private set; }
        private Process process;
        private StringBuilder output;
        private static Logger logger = LogManager.GetLogger("JobSystem");
        private JobCallbackInfo callbackInfo;
        private static int nextJobId = 0;

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

            output = new StringBuilder();
            DataReceivedEventHandler outputHandler = (s, a) =>
            {
                logger.Debug($"Job {id} output: {a.Data}");
                output.AppendLine(a.Data);
            };
            process.OutputDataReceived += outputHandler;
            process.ErrorDataReceived += outputHandler;

            // add a logging message when a job finishes
            process.EnableRaisingEvents = true;
            process.Exited += (o, e) => logger.Info($"Job {id} finished executing.");

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
                if (!client.ConnectAsync(callbackInfo.Address, callbackInfo.Port).Wait(5000))
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
        }

    }

    public class JobCallbackInfo
    {
        public int Port;
        public IPAddress Address;
    }
}
