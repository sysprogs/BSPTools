using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.IO.Compression;

namespace ImportedSDKValidator
{
    class Program
    {
        class SimpleLogger : IDisposable
        {
            private StreamWriter _Writer;

            public SimpleLogger(string logFile)
            {
                _Writer = File.CreateText(logFile);
            }

            public void Dispose()
            {
                _Writer.Dispose();
            }

            public void WriteLine(string line, ConsoleColor ?color = null)
            {
                _Writer.WriteLine(line);

                if (color.HasValue)
                {
                    var oldColor = Console.ForegroundColor;
                    Console.ForegroundColor = color.Value;
                    Console.WriteLine(line);
                    Console.ForegroundColor = oldColor;
                }
                else
                    Console.WriteLine(line);
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                throw new Exception("Usage: ImportedSDKValidator <job file> <temporary directory> [/clean]");
            }

            var job = XmlTools.LoadObject<ImportedSDKValidationJob>(args[0]);
            var plugin = new KSDK2xImporter.KSDKManifestParser();

            bool cleanMode = args.Contains("/clean");

            int passed = 0, failed = 0;

            using (var log = new SimpleLogger(Path.Combine(args[1], plugin.UniqueID, "test.log")))
            {
                foreach (var sdk in job.SDKs)
                {
                    var baseDir = Path.Combine(args[1], plugin.UniqueID, Path.GetFileNameWithoutExtension(sdk.Archive));
                    var bspDir = Path.Combine(baseDir, "BSP");
                    if (cleanMode && Directory.Exists(bspDir))
                        Directory.Delete(bspDir, true);

                    if (!File.Exists(Path.Combine(bspDir, "BSP.XML")))
                    {
                        Console.WriteLine($"Extracting {sdk.Archive}...");
                        ZipFile.ExtractToDirectory(Path.Combine(Path.GetDirectoryName(args[0]), sdk.Archive), bspDir);
                    }

                    log.WriteLine($"Importing {sdk.Archive}...");

                    var sink = new ConsoleWarningSink();

                    plugin.GenerateBSPForSDK(new ImportedSDKLocation { Directory = bspDir }, sink);
                    if (sink.WarningCount > 0)
                        throw new Exception("Found warnings while importing SDK. Please review them first.");

                    var bsp = StandaloneBSPValidator.Program.LoadBSP(job.Toolchain, bspDir);
                    var vendorSamples = XmlTools.LoadObject<VendorSampleDirectory>(Path.Combine(bspDir, bsp.BSP.VendorSampleDirectoryPath, "VendorSamples.xml"));
                    vendorSamples.Path = Path.GetFullPath(Path.Combine(bspDir, "VendorSamples"));
                    log.WriteLine($"Testing {sdk.ValidatedSamples.Length} samples...");

                    foreach (var sampleID in sdk.ValidatedSamples)
                    {
                        var sample = vendorSamples.Samples.First(s => s.InternalUniqueID == sampleID);
                        var mcu = bsp.MCUs.First(m => m.ExpandedMCU.ID == sample.DeviceID);

                        var result = StandaloneBSPValidator.Program.TestVendorSampleAndUpdateDependencies(mcu, sample, Path.Combine(baseDir, "build", sampleID), null, false, StandaloneBSPValidator.BSPValidationFlags.None);
                        switch (result.Result)
                        {
                            case StandaloneBSPValidator.Program.TestBuildResult.Succeeded:
                                log.WriteLine($"{sampleID} - Succeeded");
                                passed++;
                                break;
                            case StandaloneBSPValidator.Program.TestBuildResult.Failed:
                                log.WriteLine($"{sampleID} - FAILED", ConsoleColor.Red);
                                failed++;
                                break;
                        }
                    }

                    log.WriteLine($"========================");

                }

                if (passed > 0 && failed == 0)
                    log.WriteLine($"All tests passed", ConsoleColor.Green);
                else
                    log.WriteLine($"{passed} tests passed, {failed} tests failed", ConsoleColor.Red);
            }

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }

    public class ConsoleWarningSink : IWarningSink, ISDKImportHost
    {
        public IWarningSink WarningSink => this;

        public int WarningCount { get; private set; }

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
            WarningCount++;
        }
    }


    public class ImportedSDKValidationJob
    {
        public class SDK
        {
            public string Archive;
            public string[] ValidatedSamples;
        }

        public string Toolchain;
        public SDK[] SDKs;
    }
}
