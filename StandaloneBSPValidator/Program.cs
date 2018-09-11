using BSPEngine;
using BSPGenerationTools;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Serialization;

namespace StandaloneBSPValidator
{
    public class TestedSample
    {
        public string Name;
        public string TestDirSuffix;
        public string DeviceRegex;
        public bool SkipIfNotFound;
        public bool ValidateRegisters;
        public bool DataSections;
        public PropertyDictionary2 SampleConfiguration;
        public PropertyDictionary2 FrameworkConfiguration;
        public PropertyDictionary2 MCUConfiguration;
        public string[] AdditionalFrameworks;
        public string SourceFileExtensions = "cpp;c;s";
    }

    public class DeviceParameterSet
    {
        public string DeviceRegex
        {
            get { return DeviceRegexObject?.ToString(); }
            set { DeviceRegexObject = new Regex(value, RegexOptions.IgnoreCase); }
        }

        //[XmlIgnore]
        public Regex DeviceRegexObject;

        public PropertyDictionary2 SampleConfiguration;
        public PropertyDictionary2 FrameworkConfiguration;
        public PropertyDictionary2 MCUConfiguration;
    }

    public enum RegisterRenamingMode
    {
        Normal,
        HighLow,
        WithSuffix,
    }

    public struct RegisterRenamingRule
    {
        public string RegisterSetRegex;
        public string RegisterRegex;
        public RegisterRenamingMode Mode;
        public int Offset;
    }

    public struct LoadedRenamingRule
    {
        public Regex RegisterSetRegex;
        public Regex RegisterRegex;
        public RegisterRenamingMode Mode;
        public int Offset;

        public LoadedRenamingRule(RegisterRenamingRule rule)
        {
            if (rule.RegisterSetRegex != null)
                RegisterSetRegex = new Regex($"^{rule.RegisterSetRegex}$");
            else
                RegisterSetRegex = null;

            switch (rule.Mode)
            {
                case RegisterRenamingMode.HighLow:
                    RegisterRegex = new Regex($"^({rule.RegisterRegex})(H|L)$");
                    break;
                case RegisterRenamingMode.WithSuffix:
                    RegisterRegex = new Regex($"^({rule.RegisterRegex})([0-9]+)_(.*)$");
                    break;
                default:
                    RegisterRegex = new Regex($"^({rule.RegisterRegex})([0-9]+)$");
                    break;
            }

            Mode = rule.Mode;
            Offset = rule.Offset;
        }
    }

    public class TestJob
    {
        public string DeviceRegex;
        public string SkippedDeviceRegex;
        public string ToolchainPath;
        public string BSPPath;
        public TestedSample[] Samples;
        public DeviceParameterSet[] DeviceParameterSets;
        public RegisterRenamingRule[] RegisterRenamingRules;
        public string[] NonValidatedRegisters;
        public string[] UndefinedMacros;
    }

    public class Program
    {
        static Dictionary<string, string> GetDefaultPropertyValues(PropertyList propertyList)
        {
            var properties = new Dictionary<string, string>();
            if (propertyList != null)
                foreach (var grp in propertyList.PropertyGroups)
                    foreach (var prop in grp.Properties)
                    {
                        string uniqueID = grp.UniqueID + prop.UniqueID;

                        if (prop is PropertyEntry.Enumerated)
                            properties[uniqueID] = (prop as PropertyEntry.Enumerated).SuggestionList[(prop as PropertyEntry.Enumerated).DefaultEntryIndex].InternalValue;
                        if (prop is PropertyEntry.Integral)
                            properties[uniqueID] = (prop as PropertyEntry.Integral).DefaultValue.ToString();
                        if (prop is PropertyEntry.Boolean)
                            properties[uniqueID] = (prop as PropertyEntry.Boolean).DefaultValue ? (prop as PropertyEntry.Boolean).ValueForTrue : (prop as PropertyEntry.Boolean).ValueForFalse;
                        if (prop is PropertyEntry.String)
                            properties[uniqueID] = (prop as PropertyEntry.String).DefaultValue;

                        //TODO: other types
                    }
            return properties;
        }

        public enum TestResult
        {
            Succeeded,
            Failed,
            Skipped,
        }

        static Regex RgMainMap = new Regex("^[ \t]+0x[0-9a-fA-F]+[ \t]+main$");

        class BuildTask
        {
            public string Executable;
            public string Arguments;
            public string[] AllInputs;
            public string PrimaryOutput;

            public Process Start(string mcuDir, int slot, StreamWriter logWriter)
            {
                string args = Arguments;
                args = args.Replace("$@", PrimaryOutput);
                args = args.Replace("$<", AllInputs[0]);
                args = args.Replace("$^", string.Join(" ", AllInputs));

                lock (logWriter)
                    logWriter.WriteLine($"[{slot}] {Executable} {args}");
                var proc = Process.Start(new ProcessStartInfo(Executable, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = mcuDir, RedirectStandardOutput = true, RedirectStandardError = true });
                DataReceivedEventHandler handler = (s, e) =>
                {
                    if (e.Data == null)
                        return;
                    lock (logWriter)
                        logWriter.WriteLine($"[{slot}] {e.Data}");
                };

                proc.ErrorDataReceived += handler;
                proc.OutputDataReceived += handler;
                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();
                return proc;
            }
        }

        class BuildJob
        {
            public List<BuildTask> CompileTasks = new List<BuildTask>();
            public List<BuildTask> OtherTasks = new List<BuildTask>();

            public void GenerateMakeFile(string filePath, string primaryTarget)
            {
                using (var sw = new StreamWriter(filePath))
                {
                    sw.WriteLine($"all: {primaryTarget}");
                    sw.WriteLine();
                    foreach (var task in CompileTasks.Concat(OtherTasks))
                    {
                        sw.WriteLine($"{task.PrimaryOutput}: " + string.Join(" ", task.AllInputs));
                        if (task.Arguments.Length > 7000)
                        {
                            string prefixArgs = "", extArgs = task.Arguments;

                            int idx = task.Arguments.IndexOf("$<");
                            if (idx != -1)
                            {
                                prefixArgs = task.Arguments.Substring(0, idx + 2);
                                extArgs = task.Arguments.Substring(idx + 2);
                            }

                            string rspFile = Path.ChangeExtension(Path.GetFileName(task.PrimaryOutput), ".rsp");
                            File.WriteAllText(Path.Combine(Path.GetDirectoryName(filePath), rspFile), extArgs.Replace('\\', '/').Replace("/\"", "\\\""));
                            sw.WriteLine($"\t{task.Executable} {prefixArgs} @{rspFile}");
                        }
                        else
                            sw.WriteLine($"\t{task.Executable} {task.Arguments}");
                        sw.WriteLine();
                    }
                }
            }

            [DllImport("kernel32.dll", EntryPoint = "WaitForMultipleObjects", SetLastError = true)]
            static extern int WaitForMultipleObjects(int nCount, IntPtr[] lpHandles, Boolean fWaitAll, int dwMilliseconds);

            public bool BuildFast(string projectDir, int processorCount)
            {
                Process[] slots = new Process[processorCount];
                using (var sw = new StreamWriter(Path.Combine(projectDir, "build.log")))
                {
                    foreach (var task in CompileTasks)
                    {
                        int firstEmptySlot;
                        for (; ; )
                        {
                            firstEmptySlot = Enumerable.Range(0, slots.Length).FirstOrDefault(i => slots[i]?.HasExited != false);
                            if (slots[firstEmptySlot]?.HasExited == false)
                            {
                                WaitForMultipleObjects(slots.Length, slots.Select(s => s.Handle).ToArray(), false, Timeout.Infinite);
                                continue;
                            }
                            break;
                        }

                        if (slots[firstEmptySlot] != null && slots[firstEmptySlot].ExitCode != 0)
                        {
                            // Wait for other tasks completion
                            IntPtr[] remaining = slots.Where(s => s?.HasExited == false).Select(s => s.Handle).ToArray();
                            WaitForMultipleObjects(remaining.Length, remaining, true, Timeout.Infinite);
                            return false;   //Exited with error
                        }

                        slots[firstEmptySlot] = task.Start(projectDir, firstEmptySlot, sw);
                    }


                    IntPtr[] remainingProcesses = slots.Where(s => s?.HasExited == false).Select(s => s.Handle).ToArray();
                    WaitForMultipleObjects(remainingProcesses.Length, remainingProcesses, true, Timeout.Infinite);
                    foreach (var slot in slots)
                    {
                        if (slot != null && slot.ExitCode != 0)
                            return false;   //Exited with error
                    }

                    foreach (var task in OtherTasks)
                    {
                        var proc = task.Start(projectDir, 0, sw);
                        proc.WaitForExit();
                        if (proc.ExitCode != 0)
                            return false;
                    }
                }

                return true;
            }
        }

        static IEnumerable<string> SplitDependencyFile(string fileName)
        {
            var text = File.ReadAllText(fileName);
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '\\'))
                    i++;

                if (i >= text.Length)
                    break;

                int start = i;
                if (text[i] != '\"')
                {
                    while (i < text.Length && !char.IsWhiteSpace(text[i]))
                        i++;
                }
                else
                {
                    while (i < text.Length && text[i] != '\"')
                        i++;
                }

                yield return text.Substring(start, i - start);
            }
        }

        static void FillSampleDependenciesFromDepFiles(BSPEngine.VendorSample vs, string sampleBuildDir)
        {
            vs.AllDependencies = Directory.GetFiles(sampleBuildDir, "*.d").SelectMany(f => SplitDependencyFile(f).Where(t => !t.EndsWith(":"))).Distinct().ToArray();
        }

        private static TestResult TestVendorSample(LoadedBSP.LoadedMCU mcu, BSPEngine.VendorSample vs, string mcuDir, VendorSampleDirectory sampleDir, bool codeRequiresDebugInfoFlag)
        {
            var configuredMCU = new LoadedBSP.ConfiguredMCU(mcu, GetDefaultPropertyValues(mcu.ExpandedMCU.ConfigurableProperties));
            configuredMCU.Configuration["com.sysprogs.toolchainoptions.arm.libnosys"] = "--specs=nosys.specs";
            if (configuredMCU.ExpandedMCU.FLASHSize == 0)
            {
                configuredMCU.Configuration["com.sysprogs.bspoptions.primary_memory"] = "sram";
            }

            var entries = vs.Configuration.MCUConfiguration?.Entries;
            if (entries != null)
                foreach (var e in entries)
                    configuredMCU.Configuration[e.Key] = e.Value;


            var bspDict = configuredMCU.BuildSystemDictionary(default(SystemDirectories));
            bspDict["PROJECTNAME"] = "test";
            bspDict["SYS:VSAMPLE_DIR"] = sampleDir.Path;
            var prj = new GeneratedProject(configuredMCU, vs, mcuDir, bspDict, vs.Configuration.Frameworks ?? new string[0]);

            var projectCfg = PropertyDictionary2.ReadPropertyDictionary(vs.Configuration.MCUConfiguration);

            var frameworkCfg = PropertyDictionary2.ReadPropertyDictionary(vs.Configuration.Configuration);
            foreach (var k in projectCfg.Keys)
                bspDict[k] = projectCfg[k];
            var frameworkIDs = vs.Configuration.Frameworks?.ToDictionary(fw => fw, fw => true);
            prj.AddBSPFilesToProject(bspDict, frameworkCfg, frameworkIDs);
            var flags = prj.GetToolFlags(bspDict, frameworkCfg, frameworkIDs);

            if (flags.LinkerScript != null && !Path.IsPathRooted(flags.LinkerScript))
            {
                flags.LinkerScript = Path.Combine(VariableHelper.ExpandVariables(vs.Path, bspDict, frameworkCfg), flags.LinkerScript).Replace('\\', '/');
            }

            //ToolFlags flags = new ToolFlags { CXXFLAGS = "  ", COMMONFLAGS = "-mcpu=cortex-m3  -mthumb", LDFLAGS = "-Wl,-gc-sections -Wl,-Map," + "test.map", CFLAGS = "-ffunction-sections -Os -MD" };

            flags.CFLAGS += " -MD";
            flags.CXXFLAGS += " -MD";

            if (codeRequiresDebugInfoFlag)
            {
                flags.CFLAGS += " -ggdb";
                flags.CXXFLAGS += " -ggdb";
            }

            flags.IncludeDirectories = LoadedBSP.Combine(flags.IncludeDirectories, vs.IncludeDirectories).Distinct().ToArray();
            flags.PreprocessorMacros = LoadedBSP.Combine(flags.PreprocessorMacros, vs.PreprocessorMacros);

            flags = LoadedBSP.ConfiguredMCU.ExpandToolFlags(flags, bspDict, null);

            Dictionary<string, bool> sourceExtensions = new Dictionary<string, bool>(StringComparer.InvariantCultureIgnoreCase);
            sourceExtensions.Add("c", true);
            sourceExtensions.Add("cpp", true);
            sourceExtensions.Add("s", true);

            return BuildAndRunValidationJob(mcu, mcuDir, false, null, prj, flags, sourceExtensions, null, null, vs);
        }

        private static TestResult TestMCU(LoadedBSP.LoadedMCU mcu, string mcuDir, TestedSample sample, DeviceParameterSet extraParameters, LoadedRenamingRule[] renameRules, string[] nonValidateReg, string[] pUndefinedMacros)
        {
            const int RepeatCount = 20;
            for (var i = 0; i < RepeatCount; ++i)
            {
                if (!Directory.Exists(mcuDir))
                {
                    break;
                }
                Console.WriteLine("Deleting " + mcuDir + "...");
                Directory.Delete(mcuDir, true);
                if (i == RepeatCount - 1)
                {
                    throw new Exception("Cannot remove folder!");
                }
                Thread.Sleep(50);
            }
            for (var i = 0; i < RepeatCount; ++i)
            {
                if (Directory.Exists(mcuDir))
                {
                    break;
                }
                Directory.CreateDirectory(mcuDir);
                if (i == RepeatCount - 1)
                {
                    throw new Exception("Cannot create folder!");
                }
                Thread.Sleep(50);
            }

            var configuredMCU = new LoadedBSP.ConfiguredMCU(mcu, GetDefaultPropertyValues(mcu.ExpandedMCU.ConfigurableProperties));
            if (configuredMCU.ExpandedMCU.FLASHSize == 0)
            {
                configuredMCU.Configuration["com.sysprogs.bspoptions.primary_memory"] = "sram";
            }

            var samples = mcu.BSP.GetSamplesForMCU(mcu.ExpandedMCU.ID, false);
            LoadedBSP.LoadedSample sampleObj;
            if (string.IsNullOrEmpty(sample.Name))
                sampleObj = samples[0];
            else
                sampleObj = samples.FirstOrDefault(s => s.Sample.Name == sample.Name);

            if (sampleObj == null)
            {
                if (sample.SkipIfNotFound)
                {
                    Directory.Delete(mcuDir, true);
                    return TestResult.Skipped;
                }
                else
                    throw new Exception("Cannot find sample: " + sample.Name);
            }

            string[] frameworks = sampleObj.Sample.RequiredFrameworks ?? new string[0];


            //frameworkCfg["com.sysprogs.bspoptions.stm32.freertos.heap"] = "heap_4";
            //frameworkCfg["com.sysprogs.bspoptions.stm32.freertos.portcore"] = "CM0";
            //frameworkCfg["com.sysprogs.bspoptions.stm32.usb.devclass"] = "CDC";
            //frameworkCfg["com.sysprogs.bspoptions.stm32.usb.speed"] = "FS";

            var configuredSample = new ConfiguredSample
            {
                Sample = sampleObj,
                Parameters = GetDefaultPropertyValues(sampleObj.Sample.ConfigurableProperties),
                Frameworks = (sampleObj.Sample.RequiredFrameworks == null) ? null :
                sampleObj.Sample.RequiredFrameworks.Select(fwId =>
                {
                    return configuredMCU.BSP.BSP.Frameworks.First(fwO => fwO.ID == fwId || fwO.ClassID == fwId && fwO.IsCompatibleWithMCU(configuredMCU.ExpandedMCU.ID));
                }).ToList(),
                FrameworkParameters = new Dictionary<string, string>(),
            };


            ApplyConfiguration(configuredMCU.Configuration, extraParameters?.MCUConfiguration, sample.MCUConfiguration);

            //configuredSample.Parameters["com.sysprogs.examples.ledblink.LEDPORT"] = "GPIOA";
            //configuredSample.Parameters["com.sysprogs.examples.stm32.LEDPORT"] = "GPIOA";
            //configuredSample.Parameters["com.sysprogs.examples.stm32.freertos.heap_size"] = "0";

            var bspDict = configuredMCU.BuildSystemDictionary(default(SystemDirectories));
            bspDict["PROJECTNAME"] = "test";

            if (configuredSample.Frameworks != null)
                foreach (var fw in configuredSample.Frameworks)
                {
                    if (fw.AdditionalSystemVars != null)
                        foreach (var kv in fw.AdditionalSystemVars)
                            bspDict[kv.Key] = kv.Value;
                    if (fw.ConfigurableProperties != null)
                    {
                        var defaultFwConfig = GetDefaultPropertyValues(fw.ConfigurableProperties);
                        if (defaultFwConfig != null)
                            foreach (var kv in defaultFwConfig)
                                configuredSample.FrameworkParameters[kv.Key] = kv.Value;
                    }
                }

            if (sampleObj.Sample?.DefaultConfiguration?.Entries != null)
                foreach (var kv in sampleObj.Sample.DefaultConfiguration.Entries)
                    configuredSample.FrameworkParameters[kv.Key] = kv.Value;

            ApplyConfiguration(configuredSample.FrameworkParameters, extraParameters?.FrameworkConfiguration, sample.FrameworkConfiguration);
            ApplyConfiguration(configuredSample.Parameters, extraParameters?.SampleConfiguration, sample.SampleConfiguration);

            var prj = new GeneratedProject(mcuDir, configuredMCU, frameworks) { DataSections = sample.DataSections };
            prj.DoGenerateProjectFromEmbeddedSample(configuredSample, false, bspDict);
            Dictionary<string, bool> frameworkIDs = new Dictionary<string, bool>();
            if (frameworks != null)
                foreach (var fw in frameworks)
                    frameworkIDs[fw] = true;
            if (sample.AdditionalFrameworks != null)
                foreach (var fw in sample.AdditionalFrameworks)
                    frameworkIDs[fw] = true;

            prj.AddBSPFilesToProject(bspDict, configuredSample.FrameworkParameters, frameworkIDs);
            var flags = prj.GetToolFlags(bspDict, configuredSample.FrameworkParameters, frameworkIDs);
          //  if(sampleObj.Sample.LinkerScript!=null)
           //     flags.LinkerScript = sampleObj.Sample.LinkerScript;

            if (!string.IsNullOrEmpty(configuredSample.Sample.Sample.LinkerScript))
                flags.LinkerScript = VariableHelper.ExpandVariables(configuredSample.Sample.Sample.LinkerScript, bspDict, configuredSample.FrameworkParameters);

            flags.COMMONFLAGS += " -save-temps ";
            Dictionary<string, bool> sourceExtensions = new Dictionary<string, bool>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var ext in sample.SourceFileExtensions.Split(';'))
                sourceExtensions[ext] = true;

            return BuildAndRunValidationJob(mcu, mcuDir, sample.ValidateRegisters, renameRules, prj, flags, sourceExtensions, nonValidateReg, pUndefinedMacros);
        }

        private static TestResult BuildAndRunValidationJob(LoadedBSP.LoadedMCU mcu, string mcuDir, bool validateRegisters, LoadedRenamingRule[] renameRules, GeneratedProject prj, ToolFlags flags, Dictionary<string, bool> sourceExtensions, string[] nonValidateReg, string[] UndefinedMacros, BSPEngine.VendorSample vendorSample = null)
        {
            BuildJob job = new BuildJob();
            string prefix = string.Format("{0}\\{1}\\{2}-", mcu.BSP.Toolchain.Directory, mcu.BSP.Toolchain.Toolchain.BinaryDirectory, mcu.BSP.Toolchain.Toolchain.GNUTargetID);

            job.OtherTasks.Add(new BuildTask
            {
                Executable = prefix + "g++",
                Arguments = $"{flags.StartGroup} {flags.EffectiveLDFLAGS} $^ {flags.EndGroup} -o $@",
                AllInputs = prj.SourceFiles.Where(f => sourceExtensions.ContainsKey(Path.GetExtension(f).TrimStart('.')))
                .Select(f => Path.ChangeExtension(Path.GetFileName(f), ".o"))
                .Concat(prj.SourceFiles.Where(f => f.EndsWith(".a", StringComparison.InvariantCultureIgnoreCase)))
                .ToArray(),
                PrimaryOutput = "test.elf",
            });

            job.OtherTasks.Add(new BuildTask
            {
                Executable = prefix + "objcopy",
                Arguments = "-O binary $< $@",
                AllInputs = new[] { "test.elf" },
                PrimaryOutput = "test.bin",
            });


            foreach (var sf in prj.SourceFiles)
            {
                var sfE = sf.Replace('\\', '/');
                string ext = Path.GetExtension(sf);
                if (!sourceExtensions.ContainsKey(ext.TrimStart('.')))
                {
                    if (ext != ".txt" && ext != ".a" && ext != ".h")
                        Console.WriteLine($"#{sf} is not a recognized source file");
                }
                else
                {
                    bool isCpp = ext.ToLower() != ".c";
                    string obj = Path.ChangeExtension(Path.GetFileName(sfE), ".o");
                    job.CompileTasks.Add(new BuildTask
                    {
                        PrimaryOutput = Path.ChangeExtension(Path.GetFileName(sfE), ".o"),
                        AllInputs = new[] { sfE },
                        Executable = prefix + (isCpp ? "g++" : "gcc"),
                        Arguments = $"-c $< { (isCpp ? "-std=gnu++11 " : " ")} {flags.GetEffectiveCFLAGS(isCpp, ToolFlags.FlagEscapingMode.ForMakefile)} -o {obj}".Replace('\\', '/').Replace("/\"", "\\\""),

                    });
                }
            }


            bool errorsFound = false;
            foreach (var g in job.CompileTasks.GroupBy(t => t.PrimaryOutput.ToLower()))
            {
                if (g.Count() > 1)
                {
                    Console.WriteLine($"ERROR: {g.Key} corresponds to the following files:");
                    foreach (var f in g)
                        Console.WriteLine("\t" + f.AllInputs.FirstOrDefault());
                    errorsFound = true;
                }
            }

            if (errorsFound)
                throw new Exception("Multiple source files with the same name found");

            job.GenerateMakeFile(Path.Combine(mcuDir, "Makefile"), "test.bin");

            if (!string.IsNullOrEmpty(mcu.MCUDefinitionFile) && validateRegisters)
            {
                string firstSrcFileInPrjDir = prj.SourceFiles.First(fn => Path.GetDirectoryName(fn) == mcuDir);
                InsertRegisterValidationCode(firstSrcFileInPrjDir, XmlTools.LoadObject<MCUDefinition>(mcu.MCUDefinitionFile), renameRules, nonValidateReg, UndefinedMacros);
            }

            Console.Write("Building {0}...", Path.GetFileName(mcuDir));
            bool buildSucceeded;
            if (true)
            {
                var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + Path.Combine(mcu.BSP.Toolchain.Directory, mcu.BSP.Toolchain.Toolchain.BinaryDirectory, "make.exe") + " -j" + Environment.ProcessorCount + " > build.log 2>&1") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = mcuDir });
                proc.WaitForExit();
                buildSucceeded = proc.ExitCode == 0;
            }
            else
            {
                buildSucceeded = job.BuildFast(mcuDir, Environment.ProcessorCount);
            }

            bool success = false;
            string mapFile = Path.Combine(mcuDir, GeneratedProject.MapFileName);
            if (buildSucceeded && File.Exists(mapFile))
            {
                success = File.ReadAllLines(Path.Combine(mcuDir, mapFile)).Where(l => RgMainMap.IsMatch(l)).Count() > 0;

                if (success)
                {
                    string binFile = Path.Combine(mcuDir, "test.bin");
                    using (var fs = File.Open(binFile, FileMode.Open))
                        if (fs.Length < 512)
                            success = false;
                }
            }

            if (!success)
                return TestResult.Failed;

            if (vendorSample != null)
            {
                FillSampleDependenciesFromDepFiles(vendorSample, mcuDir);
            }

            Directory.Delete(mcuDir, true);
            return TestResult.Succeeded;
        }

        private static void ApplyConfiguration(Dictionary<string, string> dict, PropertyDictionary2 values, PropertyDictionary2 values2 = null)
        {
            if (values?.Entries != null)
                foreach (var kv in values.Entries)
                    dict[kv.Key] = kv.Value;
            if (values2?.Entries != null)
                foreach (var kv in values2.Entries)
                    dict[kv.Key] = kv.Value;
        }

        class TestResults : IDisposable
        {
            StreamWriter _Writer;
            string _CurSample;
            Dictionary<string, TestResult> _ThisTestResults = new Dictionary<string, TestResult>();

            Dictionary<string, Dictionary<string, TestResult>> _ResultMap = new Dictionary<string, Dictionary<string, TestResult>>();

            public TestResults(string fn)
            {
                _Writer = new StreamWriter(fn);
                _Writer.AutoFlush = true;
            }

            public void Dispose()
            {
                _Writer.WriteLine();
                _Writer.WriteLine("--- Summary ---");
                _Writer.WriteLine();
                foreach (var kv in _ResultMap)
                {
                    string failed = string.Join(" ", kv.Value.Where(kv2 => kv2.Value == TestResult.Failed).Select(kv2 => kv2.Key));
                    if (failed != "")
                        failed = ", failed on: ";
                    _Writer.WriteLine("{0} succeeded on {1} devices{2}", kv.Key, kv.Value.Where(kv2 => kv2.Value == TestResult.Succeeded).Count(), failed);
                    _Writer.WriteLine("Total test: {0}, failed: {1}", kv.Value.Count(), kv.Value.Where(kv2 => kv2.Value == TestResult.Failed).Count());
                }
                _Writer.Dispose();
            }

            internal void BeginSample(string name)
            {
                _Writer.WriteLine("Testing {0}...", name);
                _CurSample = name;
                _ThisTestResults = new Dictionary<string, TestResult>();
            }

            internal void ExceptionSample(string strExc, string data)
            {
                _Writer.WriteLine("\t{0}: {1}", strExc, data);
            }

            internal void LogTestResult(string mcuID, TestResult result)
            {
                _Writer.WriteLine("\t{0}: {1}", mcuID, result);
                _ThisTestResults[mcuID] = result;
            }

            internal void EndSample()
            {
                _ResultMap[_CurSample] = _ThisTestResults;
            }

        }

        public struct TestStatistics
        {
            public int Passed, Failed;
        }

        public static TestStatistics TestVendorSamples(VendorSampleDirectory samples, string bspDir, string temporaryDirectory, double testProbability = 1, bool codeRequiresDebugInfoFlag = false)
        {
            string defaultToolchainID = "SysGCC-arm-eabi-7.2.0";

            var toolchainPath = (string)Registry.CurrentUser.OpenSubKey(@"Software\Sysprogs\GNUToolchains").GetValue(defaultToolchainID);
            if (toolchainPath == null)
                throw new Exception("Cannot locate toolchain path from registry");

            var toolchain = LoadedToolchain.Load(new ToolchainSource.Other(Environment.ExpandEnvironmentVariables(toolchainPath)));
            var bsp = LoadedBSP.Load(new BSPEngine.BSPSummary(Environment.ExpandEnvironmentVariables(Path.GetFullPath(bspDir))), toolchain);
            TestStatistics stats = new TestStatistics();
            int cnt = 0, failed = 0, succeeded = 0;
            LoadedBSP.LoadedMCU[] MCUs = bsp.MCUs.ToArray();
            string outputDir = Path.Combine(temporaryDirectory, "VendorSamples");
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);
            int sampleCount = samples.Samples.Length;
            Random rng = new Random();
            using (var r = new TestResults(Path.Combine(temporaryDirectory, "bsptest.log")))
            {
                r.BeginSample("Vendor Samples");
                foreach (var vs in samples.Samples)
                {
                    LoadedBSP.LoadedMCU mcu;
                    try
                    {
                        var rgFilterID = new Regex(vs.DeviceID.Replace('x', '.'), RegexOptions.IgnoreCase);
                        mcu = bsp.MCUs.Where(f => rgFilterID.IsMatch(f.ExpandedMCU.ID)).ToArray()?.First();
                        vs.DeviceID = mcu.ExpandedMCU.ID;
                    }
                    catch (Exception ex)
                    {
                        r.ExceptionSample(ex.Message, "mcu " + vs.DeviceID + " not found in bsp");
                        Console.WriteLine("bsp have not mcu:" + vs.DeviceID);
                        continue;
                    }

                    if (testProbability < 1 && rng.NextDouble() > testProbability)
                    {
                        cnt++;
                        continue;
                    }

                    string mcuDir = Path.Combine(temporaryDirectory, "VendorSamples", vs.UserFriendlyName);
                    mcuDir += $"-{mcu.ExpandedMCU.ID}";
                    if (!Directory.Exists(mcuDir))
                        Directory.CreateDirectory(mcuDir);
                    DateTime start = DateTime.Now;

                    //If any of the source file paths in the vendor sample contains one of those strings, the sample will use the hardware FP mode.
                    string[] hwSubstrings = new[]
                    {
                        @"\ARM_CM4F\port.c",
                        @"ARM_CM7\r0p1\port.c",
                        @"CM4_GCC.a",
                        @"\ARM_CM4_MPU\port.c",
                        @"STemWin540_CM4_GCC.a",
                        @"STemWin540_CM7_GCC.a",
                        @"libPDMFilter_CM7_GCC",
                    };

                    if (vs.SourceFiles.FirstOrDefault(f => ContainsAnySubstrings(f, hwSubstrings)) != null)
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

                    vs.SourceFiles = vs.SourceFiles.Where(s => !IsNonGCCFile(vs, s)).ToArray();

                    var result = TestVendorSample(mcu, vs, mcuDir, samples, codeRequiresDebugInfoFlag);

                    Console.WriteLine($"[{(DateTime.Now - start).TotalMilliseconds:f0} msec]");

                    if (result == TestResult.Failed)
                        failed++;
                    else if (result == TestResult.Succeeded)
                        succeeded++;

                    r.LogTestResult(vs.UserFriendlyName, result);
                    cnt++;
                    Console.WriteLine("{0}: {1}% done ({2}/{3} projects, {4} failed)", vs.UserFriendlyName, (cnt * 100) / sampleCount, cnt, sampleCount, failed);
                }
                r.EndSample();
            }

            stats.Passed += succeeded;
            stats.Failed += failed;
            if (samples is ConstructedVendorSampleDirectory)
            {
                (samples as ConstructedVendorSampleDirectory).ToolchainDirectory = toolchainPath;
                (samples as ConstructedVendorSampleDirectory).BSPDirectory = Path.GetFullPath(bspDir);
            }
            return stats;
        }

        static bool ContainsAnySubstrings(string s, string[] substrings)
        {
            foreach (var sub in substrings)
                if (s.IndexOf(sub, StringComparison.InvariantCultureIgnoreCase) != -1)
                    return true;
            return false;
        }

        static bool IsNonGCCFile(VendorSample vs, string fn)
        {
            if (fn.StartsWith(vs.Path + @"\MDK-ARM", StringComparison.InvariantCultureIgnoreCase))
                return true;
            if (fn.Contains("system_nrf52"))
                return true;

            return false;
        }

        public static TestStatistics TestBSP(TestJob job, LoadedBSP bsp, string temporaryDirectory)
        {
            TestStatistics stats = new TestStatistics();
            Directory.CreateDirectory(temporaryDirectory);
            using (var r = new TestResults(Path.Combine(temporaryDirectory, "bsptest.log")))
            {
                LoadedBSP.LoadedMCU[] MCUs;
                if (job.DeviceRegex == null)
                    MCUs = bsp.MCUs.ToArray();
                else
                {
                    var rgFilter = new Regex(job.DeviceRegex);
                    MCUs = bsp.MCUs.Where(mcu => rgFilter.IsMatch(mcu.ExpandedMCU.ID)).ToArray();
                }

                if (job.SkippedDeviceRegex != null)
                {
                    var rg = new Regex(job.SkippedDeviceRegex);
                    MCUs = MCUs.Where(mcu => !rg.IsMatch(mcu.ExpandedMCU.ID)).ToArray();
                }

                var loadedRules = job.RegisterRenamingRules?.Select(rule => new LoadedRenamingRule(rule))?.ToArray();
                var noValidateReg = job.NonValidatedRegisters;

                foreach (var sample in job.Samples)
                {
                    r.BeginSample(sample.Name);
                    int cnt = 0, failed = 0, succeeded = 0;

                    var effectiveMCUs = MCUs;
                    if (!string.IsNullOrEmpty(sample.DeviceRegex))
                    {
                        Regex rgDevice = new Regex(sample.DeviceRegex);
                        effectiveMCUs = MCUs.Where(mcu => rgDevice.IsMatch(mcu.ExpandedMCU.ID)).ToArray();
                    }

                    if (sample.Name == "ValidateGenerateFramwoks")
                    {
                        var sampleFramwork = XmlTools.LoadObject<EmbeddedProjectSample>(Path.Combine(job.BSPPath, "FramworkSamples", "sample.xml"));
                        LoadedBSP.LoadedSample sampleObj1 = new LoadedBSP.LoadedSample() { Sample = sampleFramwork };
                        effectiveMCUs[0].BSP.Samples.Add(sampleObj1);
                        effectiveMCUs[0].BSP.Samples[effectiveMCUs[0].BSP.Samples.Count - 1].Directory = effectiveMCUs[0].BSP.Samples[effectiveMCUs[0].BSP.Samples.Count - 2].Directory.Remove(effectiveMCUs[0].BSP.Samples[effectiveMCUs[0].BSP.Samples.Count - 2].Directory.LastIndexOf("samples")) + "FramworkSamples";
                    }

                    foreach (var mcu in effectiveMCUs)
                    {
                        if (string.IsNullOrEmpty(mcu.ExpandedMCU.ID))
                            throw new Exception("Invalid MCU ID!");

                        var extraParams = job.DeviceParameterSets?.FirstOrDefault(s => s.DeviceRegexObject?.IsMatch(mcu.ExpandedMCU.ID) == true);

                        string mcuDir = Path.Combine(temporaryDirectory, mcu.ExpandedMCU.ID);
                        DateTime start = DateTime.Now;

                        var result = TestMCU(mcu, mcuDir + sample.TestDirSuffix, sample, extraParams, loadedRules, noValidateReg, job.UndefinedMacros);
                        Console.WriteLine($"[{(DateTime.Now - start).TotalMilliseconds:f0} msec]");
                        if (result == TestResult.Failed)
                            failed++;
                        else if (result == TestResult.Succeeded)
                            succeeded++;

                        r.LogTestResult(mcu.ExpandedMCU.ID, result);

                        cnt++;
                        Console.WriteLine("{0}: {1}% done ({2}/{3} devices, {4} failed)", sample.Name, (cnt * 100) / effectiveMCUs.Length, cnt, effectiveMCUs.Length, failed);
                    }

                    if ((succeeded + failed) == 0)
                        throw new Exception("Not a single MCU supports " + sample.Name);
                    r.EndSample();

                    stats.Passed += succeeded;
                    stats.Failed += failed;
                }
            }
            return stats;
        }

        static void Main(string[] args)
        {
            if (args[0] == "vs")
            {
                if (args.Length < 3)
                    throw new Exception("Usage: StandaloneBSPValidator vs <VendorSamples dir> <output dir>");

                if (Directory.GetFiles(args[1], "VendorSamples.xml").Count() == 0)
                {
                    foreach (var dir in Directory.GetDirectories(args[1]))
                        foreach (var es in Directory.GetFiles(dir, "VendorSamples.xml"))
                        {
                            var expandedSamples = XmlTools.LoadObject<VendorSampleDirectory>(es);
                            expandedSamples.Path = Path.GetFullPath(Path.Combine(dir, "VendorSamples"));
                            var testdir = Path.GetDirectoryName(Path.Combine(dir, "VendorSamples")).Split('\\').Reverse().ToArray()[0];
                            var ts = TestVendorSamples(expandedSamples, dir, Path.Combine(args[2], testdir));
                        }
                }
                else
                {
                    var bspDir = args[1];
                    var expandedSamples = XmlTools.LoadObject<VendorSampleDirectory>(Path.Combine(bspDir, "VendorSamples.xml"));
                    expandedSamples.Path = Path.GetFullPath(Path.Combine(bspDir, "VendorSamples"));
                    var ts = TestVendorSamples(expandedSamples, bspDir, args[2]);
                }
            }
            else
            {
                if (args.Length < 2)
                    throw new Exception("Usage: StandaloneBSPValidator <job file> <output dir>");

                var job = XmlTools.LoadObject<TestJob>(args[0]);
                job.BSPPath = job.BSPPath.Replace("$$JOBDIR$$", Path.GetDirectoryName(args[0]));
                if (job.ToolchainPath.StartsWith("["))
                {
                    job.ToolchainPath = (string)Registry.CurrentUser.OpenSubKey(@"Software\Sysprogs\GNUToolchains").GetValue(job.ToolchainPath.Trim('[', ']'));
                    if (job.ToolchainPath == null)
                        throw new Exception("Cannot locate toolchain path from registry");
                }

                var toolchain = LoadedToolchain.Load(new ToolchainSource.Other(Environment.ExpandEnvironmentVariables(job.ToolchainPath)));
                var bsp = LoadedBSP.Load(new BSPEngine.BSPSummary(Path.GetFullPath(Environment.ExpandEnvironmentVariables(job.BSPPath))), toolchain);

                TestBSP(job, bsp, args[1]);
            }
            return;


        }
        static bool IsNoValid(string pNameFrend, string[] NonValid)
        {
            if (NonValid != null)
                foreach (var st in NonValid)
                {
                    if (Regex.IsMatch(pNameFrend, st))
                        return true;
                }
            return false;
        }
        static void InsertRegisterValidationCode(string sourceFile, MCUDefinition mcuDefinition, LoadedRenamingRule[] renameRules, string[] pNonValidatedRegisters, string[] pUndefinedMacros)
        {
            if (!File.Exists(sourceFile))
                throw new Exception("File does not exist: " + sourceFile);

            if (mcuDefinition != null)
            {
                using (var sw = new StreamWriter(sourceFile, true))
                {
                    sw.WriteLine();
                    sw.WriteLine("#define STATIC_ASSERT(COND) typedef char static_assertion[(COND)?1:-1]");
                    sw.WriteLine("void ValidateOffsets()");
                    sw.WriteLine("{");
                    foreach (var regset in mcuDefinition.RegisterSets)
                        foreach (var reg in regset.Registers)
                        {
                            string regName = reg.Name;
                            if (IsNoValid(regset.UserFriendlyName, pNonValidatedRegisters))
                                continue;
                            if (IsNoValid(regName, pNonValidatedRegisters))
                                continue;
                            if (IsNoValid(regName, pUndefinedMacros))
                                sw.WriteLine($"#undef {regName}");
                            if (renameRules != null)
                                foreach (var rule in renameRules)
                                {
                                    if (rule.RegisterSetRegex?.IsMatch(regset.UserFriendlyName) != false)
                                    {
                                        var match = rule.RegisterRegex.Match(regName);
                                        if (match.Success)
                                        {
                                            switch (rule.Mode)
                                            {
                                                case RegisterRenamingMode.Normal:
                                                    regName = string.Format("{0}[{1}]", match.Groups[1], int.Parse(match.Groups[2].ToString()) + rule.Offset);
                                                    break;
                                                case RegisterRenamingMode.HighLow:
                                                    regName = string.Format("{0}[{1}]", match.Groups[1], match.Groups[2].ToString() == "H" ? 1 : 0);
                                                    break;
                                                case RegisterRenamingMode.WithSuffix:
                                                    regName = string.Format("{0}[{1}].{2}", match.Groups[1], int.Parse(match.Groups[2].ToString()) + rule.Offset, match.Groups[3]);
                                                    break;
                                            }
                                            break;
                                        }
                                    }
                                }
                            if (regset.UserFriendlyName.StartsWith("ARM Cortex M"))
                                continue;
                            if (mcuDefinition.MCUName.StartsWith("MSP432"))
                            {
                                if (regName.Contains("RESERVED"))
                                    continue;
                                sw.WriteLine("STATIC_ASSERT((unsigned)&({0}->r{1}) == {2});", regset.UserFriendlyName, regName, reg.Address);
                            }
                            else
                                sw.WriteLine("STATIC_ASSERT((unsigned)&({0}->{1}) == {2});", regset.UserFriendlyName, regName, reg.Address);
                        }
                    sw.WriteLine("}");
                }
            }
        }
    }
}
