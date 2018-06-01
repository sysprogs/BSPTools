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
            if (args[0] == "all")
            { //Parsing several sdks
                foreach (var dir in Directory.GetDirectories(args[1]))
                {
                    if (Directory.GetFiles(dir, "*manifest*.xml").Count() == 0)
                        continue;
                    Console.WriteLine("Parser " + dir);
                    var bsp1 = KSDKManifestParser.ParseKSDKManifest(dir, new ConsoleWarningSink());
                    bsp1.Save(dir);
                }
                return;
            }

            string sdkDir = args[0];
            string tempDir = args.Skip(1).FirstOrDefault();
            var bsp = KSDKManifestParser.ParseKSDKManifest(args[0], new ConsoleWarningSink());
            bsp.Save(args[0]);

            var bspDir = sdkDir;
            // Finally verify that everything builds
            /*VendorSampleDirectory expandedSamples = XmlTools.LoadObject<VendorSampleDirectory>(Path.Combine(bspDir, "VendorSamples.xml"));
            expandedSamples.Path = Path.GetFullPath(Path.Combine(bspDir, "VendorSamples"));
            StandaloneBSPValidator.Program.TestVendorSamples( expandedSamples, bspDir, tempDir, 0.1);*/
        }
    }
}
