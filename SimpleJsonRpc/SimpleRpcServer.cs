using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.IO;
using Newtonsoft.Json;

namespace SimpleJsonRpc
{
    /// <summary>
    /// Main class for the RPC system. 
    /// </summary>
    /// <remarks>The RpcServer class handles processing RPC requests. It can be used either as a stand-alone server,
    /// or with an existing solution (and just provide parsing, method lookup and execution, and result serialization). 
    /// 
    /// <br/>The general flow for using this class is:
    ///   - Mark methods you want to call using the SimpleRpcMethod attribute.
    ///   - Pass objects that have the attribute to the Register() function.
    ///   - (For internal server) Call Start() to start the server.
    ///   - (For other server) Call HandleJsonString() to handle inbound requests.
    /// 
    /// <br/><br/>This class handles parsing requests, looking up and running methods, and serializing results. If you're looking 
    /// for the functions that you can call via RPC, please see you should review the documentation for the registered object.</remarks>
    public class SimpleRpcServer
    {
        private Dictionary<string, (MethodInfo meth, object obj)> rpcMethods;
        private TcpListener serverListener;
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private CancellationTokenSource broadcastTaskCancelation;

        //track extra information for a request
        public static AsyncLocal<IPEndPoint> currentClient { get; private set; }
            = new AsyncLocal<IPEndPoint>();

        public event Action Starting;
        public event Action Stopping;


        public SimpleRpcServer()
        {
            rpcMethods = new Dictionary<string, (MethodInfo, object)>();
            broadcastTaskCancelation = new CancellationTokenSource();
        }

        /// <summary>
        /// Start the internal json rpc server, on a specific port. Optionally start broadcast server.
        /// </summary>
        /// <remarks>This starts a TcpListener on the given port, listens for inbound json rpc requests,
        /// reads to the first new line character, parses the request, calls the appropriate registered
        /// method based on the method name, and sends back the json rpc response. 
        /// 
        /// <br/><br/>If broadcastPort is specified, it will create a UDP listener on the given port, 
        /// and wait for broadcast packets with the message "SimpleJsonRpc Ping". When one is received,
        /// it will respond with the current json rpc server port. This is primarily used in lab test
        /// environments only. 
        /// 
        /// <br/><br/>If you plan on using this behind another server, you may be better served by skipping 
        /// this method and using HandleJsonString() directly.
        /// </remarks>
        /// <param name="serverPort">Port to listen for rpc requests</param>
        /// <param name="ip">IP to bind to - use null for all interfaces.</param>
        /// <param name="broadcastPort">Port to use to listen for broadcast pings. Use null to disable broadcast
        /// support.</param>
        /// <returns>Task for the running server.</returns>
        public async Task Start(int serverPort = 8000, IPAddress ip = null, int? broadcastPort = null)
        {
            ip = ip ?? IPAddress.Any;
            serverListener = new TcpListener(ip, serverPort);
            serverListener.Start();
            Starting?.Invoke();
            logger.Info("RPC Server started.");
            logger.Info("Ready for client connection");

            if (broadcastPort != null)
            {
                var token = broadcastTaskCancelation.Token;

                var broadcastTask = BroadcastResponder.StartBroadcastResponder(serverPort, 
                    broadcastPort.Value, token);
            }

            // this is our core loop - we need to:
            //    1. read the request, and parse it. 
            //    2. call the requested method
            //    3. return the response
            try
            {
                while (true)
                {
                    // 1a. get our connection and our instruction
                    var client = await serverListener.AcceptTcpClientAsync();

                    logger.Info($"Client connected: {((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString()}");

                    // 1b-3. Handle our client in a seperate thread, so we don't block inbound
                    // connections while doing work.
                    // Also, suppress compiler warning that there isn't an await here. This is a fire-and-forget method.
                    #pragma warning disable 4014
                    Task.Factory.StartNew(() => HandleClient(client));
                    #pragma warning restore 4014
                }
            }
            catch (ObjectDisposedException e)
            {
                // this happens when we stop the running server. This is normal.
                logger.Info("Server stopped.");
            }

        }
        
        /// <summary>
        /// Stops the internal json rpc server.
        /// </summary>
        public void Stop()
        {
            serverListener.Stop();

            broadcastTaskCancelation?.Cancel();

            Stopping?.Invoke();


        }

        /// <summary>
        /// Register an object that has SimpleRpcMethod annotations.
        /// </summary>
        /// <remarks>Register functions (with the SimpleRpcMethod annotation) with the rpc system.
        /// Once registered, inbound rpc requests can call methods that were annotated.
        /// 
        /// <br/>This stores a copy of the object - any inbound rpc call that matches a registered
        /// method name will be called against the provided object.</remarks>
        /// 
        /// <br/>If you register objects, that have both annotated a function with the same name,
        /// the last one will be called.
        /// <param name="rpcObject">Object with SimpleRpcMethod annotations.</param>
        public void Register(object rpcObject)
        {
            Type t = rpcObject.GetType();
            t.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(SimpleRpcMethod), true).Length > 0).ToList()
            .ForEach(x =>
            {
                rpcMethods[x.Name] = (x, rpcObject);
            });
        }

        /// <summary>
        /// Manually process an inbound json rpc request.
        /// </summary>
        /// <remarks>If you aren't using the built in server, you can use this method to manually
        /// process a json-rpc string, and have it return the result string.</remarks>
        /// <param name="jsonString">Json rpc request (as a string)</param>
        /// <returns>Json rpc result, as a string.</returns>
        public string HandleJsonString(string jsonString)
        {
            // report what we received in debug log.
            logger.Debug($"Received json: {jsonString}");

            // parse into our JsonRpcRequest
            JsonRpcRequest request = JsonConvert.DeserializeObject<JsonRpcRequest>(jsonString);

            // run the requested function
            // and build our response object
            JsonRpcResponse response = new JsonRpcResponse();
            response.id = request.id;

            try
            {
                response.result = CallMethod(request.method, request.args.ToArray());

            }
            catch (Exception e)
            {
                response.error = e.ToString();
                logger.Error(e);
            }

            return JsonConvert.SerializeObject(response);
        }

        public void HandleClient(TcpClient client)
        {
            using (client)
            using (StreamReader reader = new StreamReader(client.GetStream()))
            using (StreamWriter writer = new StreamWriter(client.GetStream()))
            {
                currentClient.Value = (IPEndPoint)client.Client.RemoteEndPoint;

                // make sure if we time out we don't hold threads
                // while waiting for clients that may have stalled.
                // But we don't want this while debugging.
#if (!DEBUG)
                client.ReceiveTimeout = 10 * 1000;
#endif
                // 1b. Get our instructions
                // and drop any client that takes more than 10 seconds after connecting
                // to send.
                string jsonString;
                try { jsonString = reader.ReadLine(); }
                catch (IOException)
                {
                    logger.Info("Connection timed out.");
                    return;
                }

                // 2. Parse the string into a json rpc request and call the appropriate method.
                // then serialize the response back into a string.
                var resultString = HandleJsonString(jsonString);
               

                // 3. send back our response
                writer.WriteLine(resultString);
                writer.Flush();

                // using block will close down connection
                logger.Info($"RPC call complete, closing connection to client {((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString()}");

                currentClient.Value = null;
            }

        }

        private object CallMethod(string rpcMethodName, object[] methodArgs)
        {
            // locate the method
            if (!rpcMethods.ContainsKey(rpcMethodName))
            {
                throw new ArgumentException($"Rpc method name: {rpcMethodName} not found in registered functions.");
            }

            MethodInfo method = rpcMethods[rpcMethodName].meth;
            var callableObject = rpcMethods[rpcMethodName].obj;

            // determine if the last item is a parameter array
            // see https://stackoverflow.com/questions/627656/determining-if-a-parameter-uses-params-using-reflection-in-c
            // and https://stackoverflow.com/questions/6484651/calling-a-function-using-reflection-that-has-a-params-parameter-methodbase
            var paramArray = method.GetParameters();
            if (paramArray.Length > 0 && paramArray.Last().IsDefined(typeof(ParamArrayAttribute), false))
            {
                // we need the object array to have a sub-array with anything after the number of normal parameters.
                // copy all the normal parameters into an object array
                object[] normalargs = new object[paramArray.Length];
                Array.Copy(methodArgs, normalargs, paramArray.Length - 1);

                // copy all the extra args into a seperate array
                Array extraArgs = Array.CreateInstance(paramArray.Last().ParameterType.GetElementType(), methodArgs.Length - (paramArray.Length - 1));
                Array.Copy(methodArgs, paramArray.Length - 1, extraArgs, 0, extraArgs.Length);

                // set the last item of the paramArray to to the extraArgs array
                normalargs[normalargs.Length - 1] = extraArgs;

                // rebind methodArgs
                methodArgs = normalargs;
            }

            // handle default args - add type missing for all arguments that are optional.
            // if we're handling parameter arrays, this isn't an issue since then every other arg was populated.
            if (paramArray.Length > methodArgs.Length)
            {
                object[] argsWithDefaults = new object[paramArray.Length];
                Array.Copy(methodArgs, argsWithDefaults, methodArgs.Length);

                for (int i = methodArgs.Length; i < paramArray.Length; i++)
                {
                    argsWithDefaults[i] = Type.Missing;
                }

                // rebind methodargs
                methodArgs = argsWithDefaults;
            }

            object result = method.Invoke(callableObject, methodArgs);
            return result;
        }

    }

    public class JsonRpcRequest
    {
        public string jsonrpc = "2.0";
        public string method;

        [JsonProperty(PropertyName = "params")]
        public List<object> args = new List<object>();

        public int id;
    }

    public class JsonRpcResponse
    {
        public string jsonrpc = "2.0";

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public object result;

        public int id;

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string error;
    }
}
