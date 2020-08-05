using BSPEngine;
using System;
using System.IO;
using System.Linq;

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
        class ConsoleWarningSink : IWarningSink, ISDKImportHost
        {
            public IWarningSink WarningSink => this;

            public bool AskWarn(string text)
            {
                throw new NotImplementedException();
            }

            public void DeleteDirectoryRecursively(string directory)
            {
                throw new NotImplementedException();
            }

            public void ExtractZIPFile(string zipFile, string targetDirectory)
            {
                throw new NotImplementedException();
            }

            public string GetDefaultDirectoryForImportedSDKs(string target)
            {
                throw new NotImplementedException();
            }

            public void LogWarning(string warning)
            {
                Console.WriteLine("Warning: " + warning);
            }

            public MCUDefinition TryParseSVDFile(string fullPath, string deviceName) => null;
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
            var parser = new KSDKManifestParser();
            parser.GenerateBSPForSDK(new ImportedSDKLocation { Directory = sdkDir }, new ConsoleWarningSink());
        }
    }
}
