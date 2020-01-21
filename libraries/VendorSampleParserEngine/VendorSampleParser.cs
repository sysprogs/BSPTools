using BSPEngine;
using BSPGenerationTools;
using Microsoft.Win32;
using StandaloneBSPValidator;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public readonly string CacheDirectory, RulesDirectory;

        public readonly string BSPDirectory;

        protected bool CodeRequiresDebugInfoFlag;
        public readonly string VendorSampleCatalogName;
        public readonly LoadedBSP BSP;

        public readonly string ReportFile;
        readonly VendorSampleTestReport _Report;

        readonly KnownSampleProblemDatabase _KnownProblems;

        protected readonly RegistryKey _SettingsKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Sysprogs\BSPTools\VendorSampleParsers");

        protected VendorSampleParser(string testedBSPDirectory, string sampleCatalogName, string subdir = null)
        {
            VendorSampleCatalogName = sampleCatalogName;

            var baseDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), @"..\.."));

            string problemClassifierFile = Path.Combine(baseDirectory, "KnownProblems.xml");
            _KnownProblems = XmlTools.LoadObject<KnownSampleProblemDatabase>(problemClassifierFile);

            BSPDirectory = Path.GetFullPath(Path.Combine(baseDirectory, testedBSPDirectory));
            CacheDirectory = Path.Combine(baseDirectory, "Cache");
            RulesDirectory = Path.Combine(baseDirectory, "Rules");
            var reportDirectory = Path.Combine(baseDirectory, "Reports");

            if (!string.IsNullOrEmpty(subdir))
            {
                CacheDirectory = Path.Combine(CacheDirectory, subdir);
                RulesDirectory = Path.Combine(RulesDirectory, subdir);
                reportDirectory = Path.Combine(reportDirectory, subdir);
            }

            Directory.CreateDirectory(CacheDirectory);
            Directory.CreateDirectory(reportDirectory);

            var toolchainType = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(BSPDirectory, LoadedBSP.PackageFileName)).GNUTargetID ?? throw new Exception("The BSP does not define GNU target ID.");

            TestDirectory = _SettingsKey.GetValue("TestDirectory") as string ?? throw new Exception("Registry settings not present. Please apply 'settings.reg'");
            ToolchainDirectory = _SettingsKey.CreateSubKey("ToolchainDirectories").GetValue(toolchainType) as string ?? throw new Exception($"Location for {toolchainType} toolchain is not configured. Please apply 'settings.reg'");

            var toolchain = LoadedToolchain.Load(new ToolchainSource.Other(Environment.ExpandEnvironmentVariables(ToolchainDirectory)));
            BSP = LoadedBSP.Load(new BSPEngine.BSPSummary(Environment.ExpandEnvironmentVariables(Path.GetFullPath(BSPDirectory))), toolchain);

            ReportFile = Path.Combine(reportDirectory, BSP.BSP.PackageVersion.Replace(".", "_") + ".xml");
            if (File.Exists(ReportFile))
                _Report = XmlTools.LoadObject<VendorSampleTestReport>(ReportFile);
            else
                _Report = new VendorSampleTestReport { BSPVersion = BSP.BSP.PackageVersion, BSPID = BSP.BSP.PackageID };
        }

        //Used to track samples that could not be parsed, so we can compare the statistics between BSP versions.
        public struct UnparseableVendorSample
        {
            public string BuildLogFile;
            public string UniqueID;
            public string ErrorDetails;
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
                @"\ARM_CM33_NTZ\non_secure\port.c",
                @"ARM_CM7\r0p1\port.c",
                @"CM4_GCC.a",
                @"CM4_GCC_wc32.a",
                @"CM78_GCC.a",
                @"CM7_GCC_wc32.a",
                @"\ARM_CM4_MPU\port.c",
                @"STemWin540_CM4_GCC.a",
                @"STemWin540_CM7_GCC.a",
                @"STemWin_CM4_wc32",
                @"STemWin_CM7_wc32",
                @"libtouchgfx-float-abi-hard.a",
                @"network_runtime.a",   //STM32MP1
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
            bool ShouldParseAnySamplesInsideDirectory(string directory);
            void OnSampleParsed(VendorSample sample);
        }

        class VendorSampleFilter : IVendorSampleFilter
        {
            class PathEntry
            {
                public Dictionary<string, PathEntry> SubEntries = new Dictionary<string, PathEntry>(StringComparer.InvariantCultureIgnoreCase);

                internal void RememberSamplePathRecursively(string[] components, int index)
                {
                    if (!SubEntries.TryGetValue(components[index], out var subEntry))
                        SubEntries[components[index]] = subEntry = new PathEntry();

                    if (++index < components.Length)
                        subEntry.RememberSamplePathRecursively(components, index);
                }
            }

            PathEntry _RootDir = new PathEntry();
            bool _ParseAll;

            public bool IsEmpty => !_ParseAll && _RootDir.SubEntries.Count == 0;

            public VendorSampleFilter()
            {
                _ParseAll = true;
            }

            public VendorSampleFilter(VendorSampleTestReport report, IEnumerable<VendorSample> alreadyDiscoveredSamples)
            {
                Dictionary<string, string> sampleDirectories = new Dictionary<string, string>();
                foreach (var s in alreadyDiscoveredSamples)
                    sampleDirectories[s.InternalUniqueID] = s.Path;

                foreach (var rec in report.Records)
                {
                    if (rec.BuildFailedExplicitly && sampleDirectories.TryGetValue(rec.UniqueID, out var dir))
                    {
                        RememberSamplePath(dir.Replace('/', '\\').Split('\\'));
                    }
                }
            }

            private void RememberSamplePath(string[] components)
            {
                _RootDir.RememberSamplePathRecursively(components, 0);
            }

            public void OnSampleParsed(VendorSample sample)
            {
            }

            public bool ShouldParseAnySamplesInsideDirectory(string directory)
            {
                if (_ParseAll)
                    return true;

                string[] components = directory.Replace('/', '\\').Split('\\');
                PathEntry e = _RootDir;
                foreach (var c in components)
                {
                    if (!e.SubEntries.TryGetValue(c, out e))
                        return false;
                }

                return true;
            }
        }

        class SingleSampleFilter : IVendorSampleFilter
        {
            private VendorSample _ExistingSample;
            private readonly string _Path;

            public SingleSampleFilter(VendorSample existingSample)
            {
                _ExistingSample = existingSample;
                _Path = Path.GetFullPath(_ExistingSample.Path);
            }

            public void OnSampleParsed(VendorSample sample)
            {
            }

            public bool ShouldParseAnySamplesInsideDirectory(string directory)
            {
                if (_Path.StartsWith(directory, StringComparison.InvariantCultureIgnoreCase))
                    return true;

                return false;
            }
        }


        private ConstructedVendorSampleDirectory BuildOrLoadSampleDirectoryAndUpdateReportForFailedSamples(string sampleListFile, string SDKdir, RunMode mode, string specificSampleName)
        {
            Console.WriteLine($"Creating sample list...");
            ConstructedVendorSampleDirectory sampleDir = null;
            bool directoryMatches = false;
            if (File.Exists(sampleListFile) || File.Exists(sampleListFile + ".gz"))
            {
                sampleDir = XmlTools.LoadObject<ConstructedVendorSampleDirectory>(sampleListFile);
                if (sampleDir.SourceDirectory == SDKdir)
                    directoryMatches = true;
            }

            if (directoryMatches && mode == RunMode.Release)
            {
                Console.WriteLine($"Loaded {sampleDir.Samples.Length} samples from cache");
                HashSet<string> blacklist = ParseBlacklistFile();
                sampleDir.Samples = sampleDir.Samples.Where(s => !blacklist.Contains(s.InternalUniqueID)).ToArray();
                return sampleDir;
            }

            IVendorSampleFilter filter;
            if (mode == RunMode.SingleSample && sampleDir != null)
            {
                var existingSample = sampleDir.Samples.FirstOrDefault(s => s.InternalUniqueID == specificSampleName) ?? throw new Exception("Unknown sample specified via command line: " + specificSampleName);
                filter = new SingleSampleFilter(existingSample);
            }
            else if (!directoryMatches || mode != RunMode.Incremental || sampleDir == null)
                filter = new VendorSampleFilter();
            else
                filter = new VendorSampleFilter(_Report, sampleDir.Samples);

            if ((filter as VendorSampleFilter)?.IsEmpty != true)
            {
                var samples = ParseVendorSamples(SDKdir, filter);

                HashSet<string> blacklist = ParseBlacklistFile();
                samples.VendorSamples = samples.VendorSamples.Where(s => !blacklist.Contains(s.InternalUniqueID)).ToArray();

                if (directoryMatches && (mode == RunMode.Incremental || mode == RunMode.SingleSample))
                {
                    //We don't update the report yet, even if the samples were previously marked as 'parse failed'. 
                    //This status will get overridden once the samples are tested.

                    Dictionary<string, VendorSample> newSampleDict = new Dictionary<string, VendorSample>();
                    foreach (var vs in samples.VendorSamples)
                        newSampleDict[vs.InternalUniqueID] = vs;

                    for (int i = 0; i < sampleDir.Samples.Length; i++)
                        if (newSampleDict.TryGetValue(sampleDir.Samples[i].InternalUniqueID, out var newSampleDefinition))
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
                    foreach (var fs in samples.FailedSamples)
                        StoreError(_Report.ProvideEntryForSample(fs.UniqueID), fs.BuildLogFile, VendorSamplePass.InitialParse);
                }

                XmlTools.SaveObject(sampleDir, sampleListFile);
            }
            else if (sampleDir == null)
                throw new Exception("Unexpected null sample directory");

            return sampleDir;
        }

        private void StoreError(VendorSampleTestReport.Record record, string buildLogFile, VendorSamplePass pass)
        {
            var prevPass = pass - 1;
            if (record.LastSucceededPass > prevPass)
                record.LastSucceededPass = prevPass;

            record.BuildFailedExplicitly = true;
            record.KnownProblemID = _KnownProblems.TryClassifyError(buildLogFile)?.ID;
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
                if (!record.BuildFailedExplicitly)
                    _FileStream.WriteLine($"{record.UniqueID} succeded in {record.BuildDuration} milliseconds");
                else
                {
                    _FileStream.WriteLine($"{record.UniqueID} FAILED");
                    _FailedSamples++;
                }
            }
        }


        Program.TestStatistics TestVendorSamplesAndUpdateReportAndDependencies(VendorSample[] samples, string sampleDirPath, VendorSamplePass pass, Predicate<VendorSample> keepDirectoryAfterSuccessfulBuild = null, double testProbability = 1, BSPValidationFlags validationFlags = BSPValidationFlags.None)
        {
            Console.WriteLine($"Building {samples.Length} samples...");
            if (pass != VendorSamplePass.RelocatedBuild && pass != VendorSamplePass.InPlaceBuild)
                throw new Exception("Invalid build pass: " + pass);

            int line = Console.CursorTop;

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
                        var rgFilterID = new Regex(vs.DeviceID.Replace('x', '.').Replace("_DEBUG", "").Replace("_MBR", "").Replace("_S132", "").Replace("_S140", ""), RegexOptions.IgnoreCase);
                        //We need to find the shortest MCU name that matches the mask (e.g. for CC3220S and CC3220SF we should pick CC3220S).
                        mcu = BSP.MCUs.OrderBy(m => m.ExpandedMCU.ID.Length).Where(f => rgFilterID.IsMatch(f.ExpandedMCU.ID)).ToArray()?.First();
                        vs.DeviceID = mcu.ExpandedMCU.ID;
                    }
                    catch
                    {
                        logger.HandleError($"Could not find {vs.DeviceID} MCU  , Project: {vs.UserFriendlyName} ");
                        continue;
                    }

                    if (testProbability < 1 && rng.NextDouble() > testProbability)
                    {
                        samplesProcessed++;
                        continue;
                    }

                    VendorSampleTestReport.Record record = _Report.ProvideEntryForSample(vs.InternalUniqueID);

                    string mcuDir = Path.Combine(outputDir, record.UniqueID);
                    DateTime start = DateTime.Now;

                    var thisSampleFlags = validationFlags;
                    if (keepDirectoryAfterSuccessfulBuild?.Invoke(vs) == true)
                        thisSampleFlags |= BSPValidationFlags.KeepDirectoryAfterSuccessfulTest;

                    var result = StandaloneBSPValidator.Program.TestVendorSampleAndUpdateDependencies(mcu, vs, mcuDir, sampleDirPath, CodeRequiresDebugInfoFlag, thisSampleFlags);
                    record.BuildDuration = (int)(DateTime.Now - start).TotalMilliseconds;
                    record.TimeOfLastBuild = DateTime.Now;

                    if (result.Result != StandaloneBSPValidator.Program.TestBuildResult.Succeeded)
                    {
                        StoreError(record, result.LogFile, pass);
                        samplesFailed++;
                    }
                    else
                    {
                        record.BuildFailedExplicitly = false;
                        record.LastSucceededPass = pass;
                    }

                    logger.LogSampleResult(record);
                    samplesProcessed++;

                    var timePerSample = (DateTime.Now - passStartTime).TotalMilliseconds / samplesProcessed;

                    string displayedSampleName = record.UniqueID;
                    int maxNameLength = 50;
                    if (displayedSampleName.Length > maxNameLength)
                        displayedSampleName = displayedSampleName.Substring(0, maxNameLength - 3) + "...";

                    List<KeyValuePair<string, string>> fields = new List<KeyValuePair<string, string>>();
                    fields.Add(new KeyValuePair<string, string>("Pass:", pass.ToString()));
                    fields.Add(new KeyValuePair<string, string>("Current sample:", displayedSampleName));
                    fields.Add(new KeyValuePair<string, string>("Samples processed:", $"{samplesProcessed}/{sampleCount}"));
                    fields.Add(new KeyValuePair<string, string>("Average time per sample:", $"{timePerSample:f0} msec"));
                    fields.Add(new KeyValuePair<string, string>("Failed samples:", $"{samplesFailed}"));
                    var remainingTime = TimeSpan.FromMilliseconds(timePerSample * (sampleCount - samplesProcessed));

                    fields.Add(new KeyValuePair<string, string>("ETA:", $"{remainingTime.Hours:d}:{remainingTime.Minutes:d2}:{remainingTime.Seconds:d2}"));

                    Console.SetCursorPosition(0, line);
                    OutputKeyValueList(fields);

                    int maxWidth = Console.WindowWidth - 2;
                    int progressWidth = (int)((double)maxWidth * samplesProcessed) / sampleCount;
                    Console.WriteLine("[" + new string('#', progressWidth).PadRight(maxWidth) + "]");
                }
            }

            return new StandaloneBSPValidator.Program.TestStatistics { Passed = sampleCount - samplesFailed, Failed = samplesFailed };
        }

        public static void OutputKeyValueList(List<KeyValuePair<string, string>> fields)
        {
            int col1Width = fields.Max(kv => kv.Key.Length);
            foreach (var kv in fields)
                Console.WriteLine((kv.Key.PadRight(col1Width + 1) + kv.Value).PadRight(Console.WindowWidth - 1));
        }

        protected abstract VendorSampleRelocator CreateRelocator(ConstructedVendorSampleDirectory sampleDir);

        const string VendorSampleDirectoryName = "VendorSamples";

        enum RunMode
        {
            Invalid,
            Incremental,
            Release,
            CleanRelease,
            SingleSample,
            UpdateErrors,
        }

        public void Run(string[] args)
        {
            string SDKdir = null;
            string specificSampleName = null;
            RunMode mode = RunMode.Invalid;

            foreach (var arg in args)
            {
                string singlePrefix = "/single:";
                if (arg.StartsWith(singlePrefix, StringComparison.InvariantCultureIgnoreCase))
                {
                    mode = RunMode.SingleSample;
                    specificSampleName = arg.Substring(singlePrefix.Length);
                }
                else if (arg.StartsWith("/"))
                {
                    mode = Enum.GetValues(typeof(RunMode)).OfType<RunMode>().First(v => v.ToString().ToLower() == arg.Substring(1).ToLower());
                }
                else
                    SDKdir = arg;
            }

            if (SDKdir == null || mode == RunMode.Invalid)
            {
                Console.WriteLine($"Usage: {Path.GetFileName(Assembly.GetEntryAssembly().Location)} <mode> <SW package directory>");
                Console.WriteLine($"Modes:");
                Console.WriteLine($"       /incremental   - Only retest/rebuild previously failed samples.");
                Console.WriteLine($"                       This doesn't update the BSP archive.");
                Console.WriteLine($"       /release       - Reuse cached definitions, retest all samples. Update BSP.");
                Console.WriteLine($"       /cleanRelease  - Reparse/retest all samples. Update BSP.");
                Console.WriteLine($"       /updateErrors  - Re-categorize errors based on KnownProblems.xml");
                Console.WriteLine($"       /single:<name> - Run all phases of just one sample.");
                Console.WriteLine($"Press any key to continue...");
                Console.ReadKey();
                Environment.ExitCode = 1;
                return;
            }

            if (mode == RunMode.Incremental)
            {
                Console.WriteLine("*********************** WARNING ************************");
                Console.WriteLine("* Vendor sample parser is running in incremental mode. *");
                Console.WriteLine("* Only retested samples will be saved to BSP!          *");
                Console.WriteLine("* Re-run in /release mode to build a releasable BSP.   *");
                Console.WriteLine("********************************************************");
            }

            if (mode == RunMode.UpdateErrors)
            {
                foreach (var rec in _Report.Records)
                {
                }

                XmlTools.SaveObject(_Report, ReportFile);

                return;
            }

            string archiveName = string.Format("{0}-{1}.vgdbxbsp", BSP.BSP.PackageID.Split('.').Last(), BSP.BSP.PackageVersion);
            string archiveFilePath = Path.Combine(BSPDirectory, archiveName);
            if (File.Exists(archiveFilePath))
                File.Delete(archiveFilePath);

            string sampleListFile = Path.Combine(CacheDirectory, "Samples.xml");

            var sampleDir = BuildOrLoadSampleDirectoryAndUpdateReportForFailedSamples(sampleListFile, SDKdir, mode, specificSampleName);
            Dictionary<string, string> encounteredIDs = new Dictionary<string, string>();

            foreach (var vs in sampleDir.Samples)
            {
                if (encounteredIDs.TryGetValue(vs.InternalUniqueID, out var dir))
                    throw new Exception("Duplicate sample for " + vs.InternalUniqueID);
                encounteredIDs[vs.InternalUniqueID] = vs.Path;

                var rec = _Report.ProvideEntryForSample(vs.InternalUniqueID);
                if (rec.LastSucceededPass < VendorSamplePass.InitialParse)
                    rec.LastSucceededPass = VendorSamplePass.InitialParse;
            }

            //We cache unadjusted sample definitions to allow tweaking the adjusting code without the need to reparse everything.
            Console.WriteLine("Adjusting sample properties...");
            foreach (var vs in sampleDir.Samples)
            {
                AdjustVendorSampleProperties(vs);
                if (vs.Path == null)
                    throw new Exception("Missing sample path for " + vs.UserFriendlyName);
            }

            VendorSample[] pass1Queue, insertionQueue;

            switch (mode)
            {
                case RunMode.Incremental:
                    pass1Queue = insertionQueue = sampleDir.Samples.Where(s => _Report.ShouldBuildIncrementally(s.InternalUniqueID, VendorSamplePass.InPlaceBuild)).ToArray();
                    break;
                case RunMode.Release:
                    insertionQueue = sampleDir.Samples;
                    if (sampleDir.Samples.FirstOrDefault(s => s.AllDependencies != null) == null)
                        pass1Queue = sampleDir.Samples;
                    else
                        pass1Queue = new VendorSample[0];
                    break;
                case RunMode.CleanRelease:
                    pass1Queue = insertionQueue = sampleDir.Samples;
                    break;
                case RunMode.SingleSample:
                    pass1Queue = insertionQueue = sampleDir.Samples.Where(s => s.InternalUniqueID == specificSampleName).ToArray();
                    if (pass1Queue.Length == 0)
                        throw new Exception("No samples match " + specificSampleName);
                    break;
                default:
                    throw new Exception("Invalid run mode");
            }

            if (pass1Queue.Length > 0)
            {
                //Test the raw VendorSamples in-place and store AllDependencies
                TestVendorSamplesAndUpdateReportAndDependencies(pass1Queue, null, VendorSamplePass.InPlaceBuild, vs => _Report.HasSampleFailed(vs.InternalUniqueID), validationFlags: BSPValidationFlags.ResolveNameCollisions);

                foreach (var vs in pass1Queue)
                {
                    if (vs.Path == null)
                        throw new Exception("Missing sample path for " + vs.UserFriendlyName);
                }

                sampleDir.ToolchainDirectory = ToolchainDirectory;
                sampleDir.BSPDirectory = Path.GetFullPath(BSPDirectory);
                XmlTools.SaveObject(sampleDir, sampleListFile);
            }

            //Insert the samples into the generated BSP
            using (var reportWriter = new BSPReportWriter(CacheDirectory, "RelocationReport.txt"))
            {
                var relocator = CreateRelocator(sampleDir);
                var copiedFiles = relocator.InsertVendorSamplesIntoBSP(sampleDir, insertionQueue, BSPDirectory, reportWriter);

                var bsp = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(BSPDirectory, LoadedBSP.PackageFileName));
                bsp.VendorSampleDirectoryPath = VendorSampleDirectoryName;
                bsp.VendorSampleCatalogName = VendorSampleCatalogName;
                XmlTools.SaveObject(bsp, Path.Combine(BSPDirectory, LoadedBSP.PackageFileName));

                var reverseConditionTableFile = Path.Combine(BSPDirectory, ReverseFileConditionBuilder.ReverseConditionListFileName + ".gz");
                if (File.Exists(reverseConditionTableFile))
                {
                    Console.WriteLine("Building configuration fix database...");
                    var testDir = Path.Combine(TestDirectory, BSP.BSP.PackageID, "PassZ_AutoFixTest");
                    var fixBuilder = new ConfigurationFixDatabaseBuilder(BSP, testDir, XmlTools.LoadObject<ReverseConditionTable>(reverseConditionTableFile));
                    fixBuilder.BuildConfigurationFixDatabase(reportWriter);
                }
            }

            if (mode != RunMode.Incremental && mode != RunMode.SingleSample)
            {
                Console.WriteLine("Creating new BSP archive...");
                string statFile = Path.ChangeExtension(archiveName, ".xml");
                TarPacker.PackDirectoryToTGZ(BSPDirectory, archiveFilePath, fn => Path.GetExtension(fn).ToLower() != ".vgdbxbsp" && Path.GetFileName(fn) != statFile && !fn.Contains(ReverseFileConditionBuilder.ReverseConditionListFileName));
            }

            var vendorSampleListInBSP = Path.Combine(BSPDirectory, VendorSampleDirectoryName, "VendorSamples.xml");
            // Finally verify that everything builds
            var expandedSamples = XmlTools.LoadObject<VendorSampleDirectory>(vendorSampleListInBSP);
            expandedSamples.Path = Path.GetFullPath(Path.Combine(BSPDirectory, VendorSampleDirectoryName));

            var finalStats = TestVendorSamplesAndUpdateReportAndDependencies(expandedSamples.Samples, expandedSamples.Path, VendorSamplePass.RelocatedBuild);
            XmlTools.SaveObject(_Report, ReportFile);

            if (mode == RunMode.Incremental || mode == RunMode.SingleSample)
            {
                Console.WriteLine($"Deleting incomplete {vendorSampleListInBSP}...\n***Re-run in /release mode to produce a valid BSP.");
                File.Delete(vendorSampleListInBSP);   //Incremental mode only places the samples that are currently built.
            }

            if (finalStats.Failed == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"All {finalStats.Total} tests passed during final pass.");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"{finalStats.Failed} out of {finalStats.Total} tests failed during final pass.");
            }

            Console.ResetColor();
            Console.WriteLine("=============================================");
            Console.WriteLine($"Overall statistics for v{BSP.BSP.PackageVersion}:");
            List<KeyValuePair<string, string>> fields = new List<KeyValuePair<string, string>>();
            fields.Add(new KeyValuePair<string, string>("Total samples discovered:", sampleDir.Samples.Length.ToString()));

            int unparsableSamples = _Report.Records.Count(r => r.LastSucceededPass == VendorSamplePass.None);
            int failedAtFirstBuild = _Report.Records.Count(r => r.LastSucceededPass == VendorSamplePass.InitialParse && r.BuildFailedExplicitly);
            int failedAtSecondBuild = _Report.Records.Count(r => r.LastSucceededPass == VendorSamplePass.InPlaceBuild && r.BuildFailedExplicitly);

            fields.Add(new KeyValuePair<string, string>("Failed during initial parse attempt:", $"{unparsableSamples}/{sampleDir.Samples.Length} ({unparsableSamples * 100.0 / sampleDir.Samples.Length:f1}%)"));
            fields.Add(new KeyValuePair<string, string>("Failed during in-place build:", $"{failedAtFirstBuild}/{sampleDir.Samples.Length} ({failedAtFirstBuild * 100.0 / sampleDir.Samples.Length:f1}%)"));
            fields.Add(new KeyValuePair<string, string>("Failed during relocated build:", $"{failedAtSecondBuild}/{sampleDir.Samples.Length} ({failedAtSecondBuild * 100.0 / sampleDir.Samples.Length:f1}%)"));
            if (mode != RunMode.Incremental)
                fields.Add(new KeyValuePair<string, string>("Inserted into BSP:", expandedSamples.Samples.Length.ToString()));
            OutputKeyValueList(fields);

            if (finalStats.Failed > 0 && mode != RunMode.Incremental)
                throw new Exception("Some of the vendor samples have failed the final test. Fix this before releasing the BSP.");

            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
        }

        private HashSet<string> ParseBlacklistFile()
        {
            HashSet<string> result = new HashSet<string>();
            var file = Path.Combine(RulesDirectory, "blacklist.txt");
            if (File.Exists(file))
            {
                foreach(var rawLine in File.ReadAllLines(file))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("#") || line == "")
                        continue;

                    result.Add(line);
                }
            }
            return result;
        }
    }
}
