using System;

namespace PluginExample
{
    public class SimpleTest
    {
        public bool IsNumberEven(int number)
        {
            return (number % 2 == 0);
        
        }
        public void WriteToConsole()
        {
            System.Console.WriteLine("Really, you shouldn't write to console in a plugin...");;
        }

        public string SayHiToMe(string myName)
        {
            return $"Hello {myName}";
        }
    }
}
