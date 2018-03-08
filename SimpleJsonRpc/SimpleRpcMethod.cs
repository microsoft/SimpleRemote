using System;
using System.Collections.Generic;
using System.Text;

namespace SimpleJsonRpc
{
    [AttributeUsage(AttributeTargets.Method)]
    public class SimpleRpcMethod : System.Attribute
    {
    }
}
