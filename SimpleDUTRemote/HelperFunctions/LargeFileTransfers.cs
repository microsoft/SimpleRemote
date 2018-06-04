// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;
using NLog;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SimpleDUTCommonLibrary;

namespace SimpleDUTRemote.HelperFunctions
{
    public class LargeFileTransfers
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();

        public static void Upload(string path, TcpListener server, bool overwrite)
        {
            logger.Info($"Waiting for connection for directory transfer on port {((IPEndPoint)server.LocalEndpoint).Port}");
            var connection = server.AcceptTcpClientAsync();

#if DEBUG
            connection.Wait();
#else
            bool status = connection.Wait(10 * 1000);
            if (!status)
            {
                logger.Warn($"Timed out waiting for client to begin uploading directory. Closing port.");
                server.Stop();
                return;
            }
#endif
            server.Stop();

            using (var client = connection.Result)
            using (var stream = client.GetStream())
            {
                try
                {
                    //var bytesReceived = ZipFunctions.ReadDirectoryFromStream(stream, path, overwrite);
                    var bytesReceived = TarFunctions.ReadFileOrDirectoryFromStream(stream, path, overwrite, 
                        closeStreamWhenComplete: false);
                    logger.Info($"Successfully received {bytesReceived} bytes, written to {path}");

                    // Depending on the record size settings, tar can legally end up sending extra null blocks.
                    // If it did, and we try to shutdown the socket without reading the remaining bytes,
                    // it will trigger an RST packet. We can avoid this by clearing the inbound socket buffer.
                    var dummyBuffer = new byte[1024];
                    while (stream.DataAvailable){
                        stream.Read(dummyBuffer, 0, 1024);
                    }

                    // attempt to send back byte count to the client before shutting down the connection
                    try
                    {
                        var temp = Encoding.ASCII.GetBytes(bytesReceived.ToString() + "\r\n");

                        stream.Write(temp, 0, temp.Length);
                    }
                    catch (IOException e)
                    {
                        logger.Info("Upload was successful, but client disconnected before byte-count could be sent back.");
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, "Upload failed.");
                }
                
            }
        }

        public static void Download(string path, TcpListener server)
        {
            logger.Info($"Waiting for connection for directory transfer on port {((IPEndPoint)server.LocalEndpoint).Port}");
            var connection = server.AcceptTcpClientAsync();

#if DEBUG
            connection.Wait();
#else
            bool status = connection.Wait(10 * 1000);
            if (!status)
            {
                logger.Warn($"Timed out waiting for client to begin downloading directory. Closing port.");
                server.Stop();
                return;
            }
#endif
            server.Stop();

            using (TcpClient client = connection.Result)
            using (var stream =client.GetStream())
            { 

                try
                {
                    //var bytesSent = ZipFunctions.WriteDirectoryToStream(stream, path);
                    var bytesSent = TarFunctions.WriteFileOrDirectoryToStream(stream, path);
                    logger.Info($"Successfully sent {bytesSent} bytes, read from {path}");
                }
                catch (Exception e)
                {
                    logger.Error(e, "Download failed");
                    return;
                }
            }
        }
    }
}
