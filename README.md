# Overview
SimpleRemote is a framework designed to make device automation easier. 
It does this by providing a simple communications framework for interacting with devices under test. 
If you need to call programs or transfer files to devices as part of your testing, this framework can help you.

This project also contains SimpleJsonRpc, which is a minimalist .NET Core JSON-RPC server. 

*Note: This project is also sometimes referred to as SimpleDUTRemote in the documentation - SimpleRemote and
SimpleDUTRemote are the same thing, SimpleDUTRemote is just the older name for the tool.*

# How do I start?
This project is fully documented (with tutorials) in doxygen. To use, simply clone this code
and run `doxygen` in the project directory. We'll also have binary releases soon that will bundle the HTML
documentation pages. 

If you already have doxygen and the .NET Core CLI tools, you can build everything by running
the `BuildAll.bat` script included in this repositry. That will automatically build the client,
server, supporting libraries, and all documentation, and place it in the `output` directory. 

# Why Should I Use This?
This project was designed by the Surface team to help automate their hardware testing. The solution is
incredibly lightweight - it has minimal power impact, generates minimal network traffic, and does not require
any external servers. To deploy, simply run an exe and you're ready. 

You can think of this as shiny version of telnet and FTP, wrapped into one. 

# Security
SimpleRemote allows users to run programs and access files on the computer where it is run, with no authentication whatsoever. It was desiged to be run on test machines on closed, lab networks. 

When you start the server for the first time, it will display a security warning, and prompt you to acknowledge the security risks of using SimpleRemote. Once you acknowledge the warning, the server will write a blank file `UserWarningAcknowledged` in the same directory as the server executable, and will not display the prompt on future runs. 

If you are deploying in a lab enviornment, acknowledge and accept the risks of using this software, and want to avoid manually acknowledging the prompt on each machine, you can either:
  - Place a blank file named `UserWarningAcknowledged` in the same directory as the server exe. 
  - Start the server with the command line flag `--SuppressUserWarning`. 

# Tests
Most functions provided by this tool have associated test cases, in the `DUTRemoteTests` project. To run,
uses either Visual Studio's build in unit test runner, or run `dotnet test` from the `DUTRemoteTests` project
directory.

# Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.microsoft.com.

When you submit a pull request, a CLA-bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., label, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
