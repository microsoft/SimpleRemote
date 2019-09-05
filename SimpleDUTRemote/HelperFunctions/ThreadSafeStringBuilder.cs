using System;
using System.Collections.Generic;
using System.Text;

namespace SimpleDUTRemote.HelperFunctions
{
    public class ThreadSafeStringBuilder
    {
        private StringBuilder sb;
        private object lockobj;

        public ThreadSafeStringBuilder()
        {
            sb = new StringBuilder();
            lockobj = new object();
        }

        public override string ToString()
        {
            lock (lockobj)
            {
                return sb.ToString();
            }
        }

        public void AppendLine(string str)
        {
            lock (lockobj)
            {
                sb.AppendLine(str);
            }
        }
    }
}
