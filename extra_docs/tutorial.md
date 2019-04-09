# SimpleRemote Basic Tutorial #
This covers the basic usage of SimpleRemote, and is designed as a companion to the API documentation
provided by doxygen. 

*A quick warning about security - this tool allows you to remotely control a device without any kind of 
authentication. You should only use this tool on secured networks.*

## Deploy the Server to your DUT ##
The first step is to launch the SimpleRemote server on your DUT. Do to this, copy the compiled binaries for
your platform to the device, and launch the `SimpleRemoteConsole.exe` binary. 

Note that if need to call functions or system services that require administrator rights, you'll need
to start the server as an administrator. 

Note - the first time you run the tool, you'll likely get a prompt asking if you want to allow the tool
through the firewall. If you don't, and you have issues connecting, either manually create a firewall
exception on the device, or you can completely disable firewalls on the device by calling:

    netsh advfirewall set allprofiles state off

## Using the .NET Client ## 
The easiest way to use the server is to use the .NET Client library, which is
[documented here](@ref SimpleDUTClientLibrary.RpcClient). As a demonstration, we'll use the client
in PowerShell to show how easy it is.

*Warning for Nuget users: SimpleRemote's nuget packages require the PackageReference format, and will not work correctly if you're using the packages.config format (the default method for managing packges for .NET Framework projects). Please review the [article here](https://blog.nuget.org/20170316/NuGet-now-fully-integrated-into-MSBuild.html) for information on how to switch a project to use PackageReference format (see the section under 'What about other project types that are not .NET Core?').*

Before we begin, we'll need to give PowerShell information about our RpcClient class. To do that,
navigate to the directory containing the %SimpleDUTClientLibrary DLL, and open a PowerShell instance. Then call:

    Add-Type -Path SimpleDUTClientLibrary.dll

Once you've done that, you can create a client instance by calling:

    $client = new-object SimpleDUTClientLibrary.RpcClient -argumentlist "127.0.0.1",8000

In your case, you should replace "127.0.0.1" with the IP address if your device, and 8000 with your
server's port number (it defaults to 8000, but can be changed with command line arguments).

Note that simply creating a client object doesn't generate a connection. To do that, we'll need
to run a method. To see the available methods on the client DLL, you can call:

    $client | Get-Member

So, to launch notepad on the remote machine, and return immediately, you would call:

    $client.Run("notepad.exe")

To run a command and get output, you would use the [RunJob](@ref SimpleDUTClientLibrary.RpcClient.RunJob) function:

    $client.RunJob("systeminfo.exe")

Note that any returned standard out/standard error will have new line characters at the end of the returned string, as it would on the remote machine (this is something to be aware of if you plan on comparing the output to a known value).

## Getting More Information on the Server ##
The server uses NLog to provide information back to the user. By default, the logger will only write messages
to the terminal, and only show messages at level `Info` or higher. If you want to see more detailed information
about what the server is doing, you can set the `minlevel` option in the `Nlog.conf` file (it's in the same
folder as `SimpleRemoteConsole.exe`) to `Debug`. You may want to do this if you want to see the stdout and stderr
of a called processes as it is generated. 

Nlog can also be configured to write to files, the Windows event log, and a number of other locations. To see more
information on how to configure logging, see the [NLog Tutorial](https://github.com/NLog/NLog/wiki/Tutorial#configuration).

## Running as a Service ##
You can install SimpleRemote as a service on any Windows system. Simple launch the %SimpleRemoteConsole exe from an elevated command prompt, and include the arg `--install-service`, as well as any other flags you wish to use (such as specifying the port). By default, the service will not start automatically, unless you specify `--service-start-type auto` when installing the service.

The service can be removed from the system my running `--uninstall-service` from an elevated command prompt. 

While running with a service is useful for some applications, note that the service will not be attached to a specific user session. As such, automating graphical applications may not work as expected. 