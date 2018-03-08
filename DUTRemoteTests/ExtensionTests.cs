using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using System.Threading;
using System.Net.Sockets;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using SimpleDUTRemote;
using System.Diagnostics;
using System.Linq;

namespace DUTRemoteTests
{
    [TestClass]
    public class ExtensionTests
    {
        [TestMethod]
        [DeploymentItem(@"PluginExample.dll")]
        public void Extensions_LoadAndCallDLL_ReturnTrue()
        {
            var functions = new Functions();

            functions.PluginLoad("testId", "PluginExample.SimpleTest", "PluginExample.dll");

            bool res = (bool) functions.PluginCallMethod("testId", "IsNumberEven", 2);
            
            Assert.IsTrue(res, "Test failed, plugin thought 2 is odd.");

        }

        [TestMethod]
        [DeploymentItem(@"PluginExample.dll")]
        public void Extensions_LoadAndCallDLL_WriteToConsole()
        {
            var functions = new Functions();
            functions.PluginLoad("testId", "PluginExample.SimpleTest", "PluginExample.dll");
            var res = functions.PluginCallMethod("testId", "WriteToConsole");

            Assert.IsNull(res, "Result was not null, but the function is type void.");
        }

        [TestMethod]
        [DeploymentItem(@"PluginExample.dll")]
        public void Extensions_LoadAndCallDLL_ReturnString()
        {
            var functions = new Functions();
            functions.PluginLoad("testId", "PluginExample.SimpleTest", "PluginExample.dll");
            var res = (string) functions.PluginCallMethod("testId", "SayHiToMe", "FOO");
            Assert.IsTrue(res == "Hello FOO", "Result string was not correct.");
        }
        
    }
}