using BSPEngine;
using BSPGenerationTools;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace VendorSampleParserEngine
{
    public abstract class VendorSampleParser
    {
        public readonly string TestDirectory, ToolchainDirectory;
        public readonly string CacheDirectory;

        public readonly string BSPDirectory;

        protected bool CodeRequiresDebugInfoFlag;
        public readonly string VendorSampleCatalogName;
        public readonly LoadedBSP BSP;

        public readonly string ReportFile;
        readonly VendorSampleTestReport _Report;

        readonly KnownSampleProblemDatabase _KnownProblems;

        protected VendorSampleParser(string testedBSPDirectory, string sampleCatalogName)
        {
            VendorSampleCatalogName = sampleCatalogName;
            RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Sysprogs\BSPTools\VendorSampleParsers");

            var baseDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), @"..\.."));

            string problemClassifierFile = Path.Combine(baseDirectory, "KnownProblems.xml");
            _KnownProblems = XmlTools.LoadObject<KnownSampleProblemDatabase>(problemClassifierFile);

            BSPDirectory = Path.GetFullPath(Path.Combine(baseDirectory, testedBSPDirectory));
            CacheDirectory = Path.Combine(baseDirectory, "Cache");
            var reportDirectory = Path.Combine(baseDirectory, "Reports");
            Directory.CreateDirectory(CacheDirectory);
            Directory.CreateDirectory(reportDirectory);

            var toolchainType = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(BSPDirectory, LoadedBSP.PackageFileName)).GNUTargetID ?? throw new Exception("The BSP does not define GNU target ID.");

            TestDirectory = key.GetValue("TestDirectory") as string ?? throw new Exception("Registry settings not present. Please apply 'settings.reg'");
            ToolchainDirectory = key.CreateSubKey("ToolchainDirectories").GetValue(toolchainType) as string ?? throw new Exception($"Location for {toolchainType} toolchain is not configured. Please apply 'settings.reg'");

            var toolchain = LoadedToolchain.Load(new ToolchainSource.Other(Environment.ExpandEnvironmentVariables(ToolchainDirectory)));
            BSP = LoadedBSP.Load(new BSPEngine.BSPSummary(Environment.ExpandEnvironmentVariables(Path.GetFullPath(BSPDirectory))), toolchain);

            ReportFile = Path.Combine(reportDirectory, BSP.BSP.PackageVersion.Replace(".", "_") + ".xml");
            if (File.Exists(ReportFile))
                _Report = XmlTools.LoadObject<VendorSampleTestReport>(ReportFile);
            else
                _Report = new VendorSampleTestReport();
        }

        //Used to track samples that could not be parsed, so we can compare the statistics between BSP versions.
        public struct UnparseableVendorSample
        {
            public string BuildLogFile;
            public VendorSampleID ID;
        }

        public struct ParsedVendorSamples
        {
            public VendorSample[] VendorSamples;
            public UnparseableVendorSample[] FailedSamples;
        }

        protected abstract ParsedVendorSamples ParseVendorSamples(string SDKdir, IVendorSampleFilter filter);

        protected static bool ContainsAnySubstrings(string s, string[] substrings)
        {
            foreach (var sub in substrings)
                if (s.IndexOf(sub, StringComparison.InvariantCultureIgnoreCase) != -1)
                    return true;
            return false;
        }

        //If any of the source file paths in the vendor sample contains one of those strings, the sample will use the hardware FP mode.
        static string[] hwSubstrings = new[] {
                @"\ARM_CM4F\port.c",
                @"ARM_CM7\r0p1\port.c",
                @"CM4_GCC.a",
                @"\ARM_CM4_MPU\port.c",
                @"STemWin540_CM4_GCC.a",
                @"STemWin540_CM7_GCC.a",
                @"libPDMFilter_CM7_GCC",
            };

        protected virtual bool ShouldFileTriggerHardFloat(string path)
        {
            return ContainsAnySubstrings(path, hwSubstrings);
        }

        protected virtual void AdjustVendorSampleProperties(VendorSample vs)
        {
            if (vs.SourceFiles.FirstOrDefault(f => ShouldFileTriggerHardFloat(f)) != null)
            {
                if (vs.Configuration.MCUConfiguration != null)
                {
                    var dict = PropertyDictionary2.ReadPropertyDictionary(vs.Configuration.MCUConfiguration);
                    dict["com.sysprogs.bspoptions.arm.floatmode"] = "-mfloat-abi=hard";
                    vs.Configuration.MCUConfiguration = new PropertyDictionary2 { Entries = dict.Select(kv => new PropertyDictionary2.KeyValue { Key = kv.Key, Value = kv.Value }).ToArray() };
                }
                else
                {
                    vs.Configuration.MCUConfiguration = new PropertyDictionary2
                    {
                        Entries = new PropertyDictionary2.KeyValue[]
                            {new PropertyDictionary2.KeyValue {Key = "com.sysprogs.bspoptions.arm.floatmode", Value = "-mfloat-abi=hard"}}
                    };
                }
            }
        }

        //This interface is used to speed up reparsing of failed samples. The specific vendor sample parser can call the methods below to check whether it should spend time parsing a specific sample.
        //ShouldParseSampleForAnyDevice() could be called before ShouldParseSampleForSpecificDevice() if extracting the device name requires reading some files, or doing other time-consuming tasks.
        //The parser can return more samples than requested, or just call ShouldParseSampleForAnyDevice() and parse matching samples for all CPUs. The framework will handle this automatically.
        protected interface IVendorSampleFilter
        {
            bool ShouldParseSampleForAnyDevice(string sampleNameWithoutDevice);
            bool ShouldParseSampleForSpecificDevice(VendorSampleID sampleID);
        }

        class VendorSampleFilter : IVendorSampleFilter
        {
            HashSet<string> _NamesToParse = new HashSet<string>();
            HashSet<VendorSampleID> _IDsToParse = new HashSet<VendorSampleID>();
            bool _ParseAll;

            public VendorSampleFilter(VendorSampleTestReport report)
            {
                if (report == null)
                    _ParseAll = true;
                else
                {
                    foreach(var rec in report.Records)
                    {
                        if (rec.BuildFailed)
                        {
                            _NamesToParse.Add(rec.ID.SampleName);
                            _IDsToParse.Add(rec.ID);
                        }
                    }
                }
            }

            public bool ShouldParseSampleForAnyDevice(string sampleNameWithoutDevice)
            {
                if (_ParseAll)
                    return true;

                return _NamesToParse.Contains(sampleNameWithoutDevice);
            }

            public bool ShouldParseSampleForSpecificDevice(VendorSampleID sampleID)
            {
                if (_ParseAll)
                    return true;

                return _IDsToParse.Contains(sampleID);
            }
        }

        private ConstructedVendorSampleDirectory BuildOrLoadSampleDirectoryAndUpdateReportForFailedSamples(string sampleListFile, string SDKdir, VendorSampleReparseCondition reparseCondition)
        {
            ConstructedVendorSampleDirectory sampleDir = null;
            bool directoryMatches = false;
            if (File.Exists(sampleListFile) || File.Exists(sampleListFile + ".gz"))
            {
                sampleDir = XmlTools.LoadObject<ConstructedVendorSampleDirectory>(sampleListFile);
                if (sampleDir.SourceDirectory == SDKdir)
                    directoryMatches = true;
            }

            if (directoryMatches && reparseCondition == VendorSampleReparseCondition.ReparseIfSDKChanged)
                return sampleDir;

            File.Delete(sampleListFile);

            IVendorSampleFilter filter;
            if (!directoryMatches || reparseCondition != VendorSampleReparseCondition.ReparseFailed)
                filter = new VendorSampleFilter(null);
            else
                filter = new VendorSampleFilter(_Report);

            var samples = ParseVendorSamples(SDKdir, filter);
            foreach (var vs in samples.VendorSamples)
                AdjustVendorSampleProperties(vs);

            if (directoryMatches && reparseCondition == VendorSampleReparseCondition.ReparseFailed)
            {
                //We don't update the report yet, even if the samples were previously marked as 'parse failed'. 
                //This status will get overridden once the samples are tested.

                Dictionary<VendorSampleID, VendorSample> newSampleDict = new Dictionary<VendorSampleID, VendorSample>();
                foreach (var vs in samples.VendorSamples)
                    newSampleDict[new VendorSampleID(vs)] = vs;

                for (int i = 0; i < sampleDir.Samples.Length; i++)
                    if (newSampleDict.TryGetValue(new VendorSampleID(sampleDir.Samples[i]), out var newSampleDefinition))
                        sampleDir.Samples[i] = newSampleDefinition;
            }
            else
            {
                sampleDir = new ConstructedVendorSampleDirectory
                {
                    SourceDirectory = SDKdir,
                    Samples = samples.VendorSamples,
                };
            }

            if (samples.FailedSamples != null)
            {
                foreach(var fs in samples.FailedSamples)
                    StoreError(_Report.ProvideEntryForSample(fs.ID), fs.BuildLogFile, VendorSamplePass.InitialParse);
            }

            XmlTools.SaveObject(sampleDir, sampleListFile);
            return sampleDir;
        }

        private void StoreError(VendorSampleTestReport.Record record, string buildLogFile, VendorSamplePass pass)
        {
            record.BuildFailed = true;
            record.KnownProblemID = _KnownProblems.TryClassifyError(buildLogFile)?.ID;
            record.LastPerformedPass = pass;
        }

        public string CreateBuildDirectory(VendorSamplePass pass)
        {
            string passSubdir;

            switch (pass)
            {
                case VendorSamplePass.InitialParse:
                    passSubdir = "Initial";
                    break;
                case VendorSamplePass.InPlaceBuild:
                    passSubdir = "Pass1";
                    break;
                case VendorSamplePass.RelocatedBuild:
                    passSubdir = "Pass2";
                    break;
                default:
                    throw new Exception("Invalid test pass: " + pass);
            }

            string dir = Path.Combine(TestDirectory, BSP.BSP.PackageID, passSubdir);
            Directory.CreateDirectory(dir);
            return dir;
        }

        class RawTestLogger : IDisposable
        {
            private StreamWriter _FileStream;
            public int InternalErrors { get; private set; }
            int _FailedSamples, _TotalSamples;

            public RawTestLogger(string fileName)
            {
                _FileStream = new StreamWriter(fileName, false) { AutoFlush = true };
            }

            public void Dispose()
            {
                _FileStream.WriteLine("-----------------------------");
                if (InternalErrors == 0 && _FailedSamples == 0)
                    _FileStream.Write($"All {_TotalSamples} samples succeeded.");
                else
                {
                    _FileStream.Write($"Failed {_FailedSamples} out of {_TotalSamples} samples.");
                    if (InternalErrors != 0)
                        _FileStream.Write($"Found {InternalErrors} internal errors.");

                }
                _FileStream.Dispose();
            }

            internal void HandleError(string line)
            {
                _FileStream.WriteLine("ERROR: " + line);
                InternalErrors++;
            }

            internal void LogSampleResult(VendorSampleTestReport.Record record)
            {
                _TotalSamples++;
                if (!record.BuildFailed)
                    _FileStream.WriteLine($"{record.ID} succeded in {record.BuildDuration} milliseconds");
                else
                {
                    _FileStream.WriteLine($"{record.ID} FAILED");
                    _FailedSamples++;
                }
            }
        }


        void TestVendorSamplesAndUpdateReport(VendorSample[] samples, string sampleDirPath, VendorSamplePass pass, double testProbability = 1)
        {
            if (pass != VendorSamplePass.RelocatedBuild && pass != VendorSamplePass.InPlaceBuild)
                throw new Exception("Invalid build pass: " + pass);

            Console.Clear();

            int samplesProcessed = 0, samplesFailed = 0;
            LoadedBSP.LoadedMCU[] MCUs = BSP.MCUs.ToArray();

            string outputDir = CreateBuildDirectory(pass);
            int sampleCount = samples.Length;
            Random rng = new Random();
            DateTime passStartTime = DateTime.Now;
            using (var logger = new RawTestLogger(Path.Combine(outputDir, "test.log")))
            {
                foreach (var vs in samples)
                {
                    LoadedBSP.LoadedMCU mcu;
                    try
                    {
                        var rgFilterID = new Regex(vs.DeviceID.Replace('x', '.'), RegexOptions.IgnoreCase);
                        mcu = BSP.MCUs.Where(f => rgFilterID.IsMatch(f.ExpandedMCU.ID)).ToArray()?.First();
                        vs.DeviceID = mcu.ExpandedMCU.ID;
                    }
                    catch
                    {
                        logger.HandleError($"Could not find {vs.DeviceID} MCU");
                        continue;
                    }

                    if (testProbability < 1 && rng.NextDouble() > testProbability)
                    {
                        samplesProcessed++;
                        continue;
                    }

                    VendorSampleTestReport.Record record = _Report.ProvideEntryForSample(new VendorSampleID(vs));

                    string mcuDir = Path.Combine(outputDir, record.ID.ToString());
                    DateTime start = DateTime.Now;

                    var result = StandaloneBSPValidator.Program.TestVendorSample(mcu, vs, mcuDir, sampleDirPath, CodeRequiresDebugInfoFlag);
                    record.LastPerformedPass = pass;
                    record.BuildDuration = (int)(DateTime.Now - start).TotalMilliseconds;
                    record.TimeOfLastBuild = DateTime.Now;

                    if (result.Result != StandaloneBSPValidator.Program.TestBuildResult.Succeeded)
                    {
                        StoreError(record, result.LogFile, pass);
                        samplesFailed++;
                    }
                    else
                        record.BuildFailed = false;

                    logger.LogSampleResult(record);
                    samplesProcessed++;

                    var timePerSample = (DateTime.Now - passStartTime).TotalMilliseconds / samplesProcessed;

                    List<KeyValuePair<string, string>> fields = new List<KeyValuePair<string, string>>();
                    fields.Add(new KeyValuePair<string, string>("Pass:", pass.ToString()));
                    fields.Add(new KeyValuePair<string, string>("Current sample:", record.ID.ToString()));
                    fields.Add(new KeyValuePair<string, string>("Samples processed:", $"{samplesProcessed}/{sampleCount}"));
                    fields.Add(new KeyValuePair<string, string>("Average time per sample:", $"{timePerSample:f0} msec"));
                    fields.Add(new KeyValuePair<string, string>("Failed samples:", $"{samplesFailed}"));
                    var remainingTime = TimeSpan.FromMilliseconds(timePerSample * (sampleCount - samplesProcessed));

                    fields.Add(new KeyValuePair<string, string>("ETA:", $"{remainingTime.Hours:d}:{remainingTime.Minutes:d2}:{remainingTime.Seconds:d2}"));

                    int col1Width = fields.Max(kv => kv.Key.Length);
                    Console.SetCursorPosition(0, 0);
                    Console.WriteLine("Testing vendor samples...");
                    Console.WriteLine("=========================");
                    foreach (var kv in fields)
                    {
                        Console.WriteLine((kv.Key.PadRight(col1Width + 1) + kv.Value).PadRight(Console.WindowWidth - 1));
                    }

                    int maxWidth = Console.WindowWidth - 2;
                    int progressWidth = (int)((double)maxWidth * samplesProcessed) / sampleCount;
                    Console.WriteLine("[" + new string('#', progressWidth).PadRight(maxWidth) + "]");
                }
            }
        }

        protected abstract VendorSampleRelocator CreateRelocator(ConstructedVendorSampleDirectory sampleDir);

        const string VendorSampleDirectoryName = "VendorSamples";

        public void Run(string[] args)
        {
            if (args.Length < 1)
                throw new Exception($"Usage: {Path.GetFileName(Assembly.GetEntryAssembly().Location)} <SW package directory>");
            string SDKdir = args[0];

            string sampleListFile = Path.Combine(CacheDirectory, "Samples.xml");

            var sampleDir = BuildOrLoadSampleDirectoryAndUpdateReportForFailedSamples(sampleListFile, SDKdir, VendorSampleReparseCondition.ReparseIfSDKChanged);

            if (sampleDir.Samples.FirstOrDefault(s => s.AllDependencies != null) == null)
            {
                //Perform Pass 1 testing - test the raw VendorSamples in-place and store AllDependencies
                TestVendorSamplesAndUpdateReport(sampleDir.Samples, null, VendorSamplePass.InPlaceBuild);

                sampleDir.ToolchainDirectory = ToolchainDirectory;
                sampleDir.BSPDirectory = Path.GetFullPath(BSPDirectory);
                XmlTools.SaveObject(sampleDir, sampleListFile);
            }

            //Insert the samples into the generated BSP
            var relocator = CreateRelocator(sampleDir);
            relocator.InsertVendorSamplesIntoBSP(sampleDir, BSPDirectory);

            var bsp = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(BSPDirectory, LoadedBSP.PackageFileName));
            bsp.VendorSampleDirectoryPath = VendorSampleDirectoryName;
            bsp.VendorSampleCatalogName = VendorSampleCatalogName;
            XmlTools.SaveObject(bsp, Path.Combine(BSPDirectory, LoadedBSP.PackageFileName));

            string archiveName = string.Format("{0}-{1}.vgdbxbsp", bsp.PackageID.Split('.').Last(), bsp.PackageVersion);
            string statFile = Path.ChangeExtension(archiveName, ".xml");
            TarPacker.PackDirectoryToTGZ(BSPDirectory, Path.Combine(BSPDirectory, archiveName), fn => Path.GetExtension(fn).ToLower() != ".vgdbxbsp" && Path.GetFileName(fn) != statFile);

            // Finally verify that everything builds
            var expandedSamples = XmlTools.LoadObject<VendorSampleDirectory>(Path.Combine(BSPDirectory, VendorSampleDirectoryName, "VendorSamples.xml"));
            expandedSamples.Path = Path.GetFullPath(Path.Combine(BSPDirectory, VendorSampleDirectoryName));

            TestVendorSamplesAndUpdateReport(expandedSamples.Samples, expandedSamples.Path, VendorSamplePass.RelocatedBuild);
            XmlTools.SaveObject(_Report, ReportFile);

            if (_Report.Records.Count(r => r.LastPerformedPass == VendorSamplePass.RelocatedBuild && r.BuildFailed) > 0)
                throw new Exception("Some of the vendor samples have failed the internal test. Fix this before releasing the BSP.");
        }
    }

    public enum VendorSampleReparseCondition
    {
        ReparseAll,
        ReparseIfSDKChanged,
        ReparseFailed,
    }
}
