using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AtmelStartSDKImporter
{
    class Program
    {
        class ConsoleWarningSink : IWarningSink
        {
            public void LogWarning(string warning)
            {
                Console.WriteLine("Warning: " + warning);
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: AtmelStartSDKImporter <Extracted Project Directory>");
                Environment.ExitCode = 1;
                return;
            }

            string sdkDir = args[0];
            AtmelStartPackageParser.GenerateBSPForSTARTProject(args[0], new ConsoleWarningSink());
        }
    }
}
