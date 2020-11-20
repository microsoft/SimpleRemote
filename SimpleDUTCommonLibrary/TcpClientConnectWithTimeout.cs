using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SimpleDUTCommonLibrary
{
    public static class TcpClientConnectWithTimeout
    {
        /// <summary>
        /// Attempt to connect within a timeout.
        /// </summary>
        /// <param name="tcpClient">The TcpClient Object</param>
        /// <param name="target">The hostname or IP address to connect to.</param>
        /// <param name="port">The port to connect to.</param>
        /// <param name="millisecondsTimeout">The timeout value in milliseconds</param>
        /// <returns>True if the connection is successful, false if the connection attempt timed out or failed.</returns>
        public static bool ConnectWithTimeout(this TcpClient tcpClient, string target, int port, int millisecondsTimeout)
        {
            var asyncResult = tcpClient.BeginConnect(target, port, ar => tcpClient.EndConnect(ar), null);

            // if the wait returned true, check if the connection succeeded (and return connection status),
            // otherwise, return false.
            return asyncResult.AsyncWaitHandle.WaitOne(millisecondsTimeout)
                ? tcpClient.Connected
                : false;
        }

        /// <summary>
        /// Attempt to connect within a timeout.
        /// </summary>
        /// <param name="tcpClient">The TcpClient Object</param>
        /// <param name="target">The hostname or IP address to connect to.</param>
        /// <param name="port">The port to connect to.</param>
        /// <param name="millisecondsTimeout">The timeout value in milliseconds</param>
        /// <returns>True if the connection is successful, false if the connection attempt timed out or failed.</returns>
        public static bool ConnectWithTimeout(this TcpClient tcpClient, IPAddress target, int port, int millisecondsTimeout)
        {
            var asyncResult = tcpClient.BeginConnect(target, port, ar => tcpClient.EndConnect(ar), null);

            // if the wait returned true, check if the connection succeeded (and return connection status),
            // otherwise, return false.
            return asyncResult.AsyncWaitHandle.WaitOne(millisecondsTimeout)
                ? tcpClient.Connected
                : false;
        }
    }
}
