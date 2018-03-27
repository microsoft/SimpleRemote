# SimpleJsonRpc Overview #
SimpleRemote uses a minimal .NET Standard JSON-RPC server library called SimpleJsonRpc. This library can be
used separately from SimpleRemote. 

You can find the Doxygen documentation for the main rpc server class under SimpleJsonRpc.SimpleRpcServer

This page provides a bit more detail on how to use the library outside of SimpleRemote.

## Declaring and Registering RPC Functions ##
To allow a method to be called via RPC, you first need to add the annotation `SimpleRpcMethod`. 

Once you've created an instance of the SimpleRpcServer class, pass an instance of the class with 
annotated methods to the function SimpleJsonRpc.SimpleRpcServer.Register(). The register function
will locate any methods with the `SimpleRpcMethod` annotation, and add them into a dictionary, mapping
the method's name to that function within that class.

*Note: The system also keeps a reference to your class instance - when you call an RPC method, it is
called against the provided instance.* 

*Warning: The system tracks your function by name only - if you try to register two functions with the same
name (even if they have different signatures) only the last one will be stored.*

## Running the Server ##
You have two options for running the server. After you create an instance of SimpleRpcServer, you can
either:
  - Have SimpleRpcServer open a server socket, and listen for inbound connections with the    
    SimpleJsonRpc.SimpleRpcServer.Start() method.
  - Manually feed SimpleRpcServer json strings (which you've received somewhere else in your app), 
    using the method SimpleJsonRpc.SimpleRpcServer.HandleJsonString().

The option only changes how the server gets the json request string - the logic afterwards is identical. 

Once a json string is received, the system will look in the registered function list for a matching function,
and call it if present. 

### Broadcast Support ###
The RpcServer, if listening on its own socket, can open a second socket and listen for UDP broadcast packets. 
If an UDP datagram arrives with the message "SimpleJsonRpc Ping", the system will respond with the RpcServer's 
IPEndPoint information. 

This was desinged to help hardware test labs, where there might be a large number of devices under test
running the server, all with potentially dynamic IP addresses. It's completely optional, and is disabled
by default. You can turn it on by specifing a `broadcastPort` in the call to SimpleJsonRpc.SimpleRpcServer.Start()

If you're using SimpleJsonRpc.SimpleRpcServer.HandleJsonString() to manually process rpc requests, then there's
no option to turn on broadcast responses. 

## Limitations ##
SimpleJsonRpc does not support named parameters in JSON-RPC requests - all parameters must be passed as arguments 
in an array.