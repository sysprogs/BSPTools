using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace BSPGenerationTools
{
    //We do not want to ship BSPGenerationTools together with the importer, so we directly include the necessary files from it
    public class MCUDefinitionWithPredicate : MCUDefinition
    {
    }
}

namespace KSDK2xImporter
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
                Console.WriteLine("Usage: KSDK2xImporter <KSDK directory>");
                Environment.ExitCode = 1;
                return;
            }

            string sdkDir = args[0];
            var bsp = KSDKManifestParser.ParseKSDKManifest(args[0], new ConsoleWarningSink());
            bsp.Save(args[0]);
        }
    }
}
