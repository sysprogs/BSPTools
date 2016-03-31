using BSPEngine;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace StandaloneBSPValidator
{
    public class TestedSample
    {
        public string Name;
        public string TestDirSuffix;
        public bool SkipIfNotFound;
        public bool ValidateRegisters;
        public PropertyDictionary2 SampleConfiguration;
        public PropertyDictionary2 FrameworkConfiguration;
        public PropertyDictionary2 MCUConfiguration;
        public string[] AdditionalFrameworks;
        public string SourceFileExtensions = "cpp;c;s";
    }

    public class TestJob
    {
        public string DeviceRegex;
        public string SkippedDeviceRegex;
        public string ToolchainPath;
        public string BSPPath;
        public TestedSample[] Samples;
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
                        if (prop is PropertyEntry.Enumerated)
                            properties[prop.UniqueID] = (prop as PropertyEntry.Enumerated).SuggestionList[(prop as PropertyEntry.Enumerated).DefaultEntryIndex].InternalValue;
                        if (prop is PropertyEntry.Integral)
                            properties[prop.UniqueID] = (prop as PropertyEntry.Integral).DefaultValue.ToString();
                        if (prop is PropertyEntry.Boolean)
                            properties[prop.UniqueID] = (prop as PropertyEntry.Boolean).DefaultValue ? (prop as PropertyEntry.Boolean).ValueForTrue : (prop as PropertyEntry.Boolean).ValueForFalse;

                        //TODO: other types
                    }
            return properties;
        }

        public enum TestResult
        {
            Succeeded,
            Failed,
            Skipped
        }

        static Regex RgMainMap = new Regex("^[ \t]+0x[0-9a-fA-F]+[ \t]+main$");

        private static TestResult TestMCU(LoadedBSP.LoadedMCU mcu, string mcuDir, TestedSample sample)
        {
            if (Directory.Exists(mcuDir))
            {
                Console.WriteLine("Deleting " + mcuDir + "...");
                Directory.Delete(mcuDir, true);
            }

            Directory.CreateDirectory(mcuDir);

            var configuredMCU = new LoadedBSP.ConfiguredMCU(mcu, GetDefaultPropertyValues(mcu.ExpandedMCU.ConfigurableProperties));
            if (configuredMCU.ExpandedMCU.FLASHSize == 0)
            {
                configuredMCU.Configuration["com.sysprogs.bspoptions.primary_memory"] = "sram";
            }

            var samples = mcu.BSP.GetSamplesForMCU(mcu.ExpandedMCU.ID);
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

            string[] frameworks = sampleObj.Sample.RequiredFrameworks;

            Dictionary<string, string> frameworkCfg = new Dictionary<string, string>();
            if (sample.FrameworkConfiguration != null)
                foreach (var kv in sample.FrameworkConfiguration.Entries)
                    frameworkCfg[kv.Key] = kv.Value;

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
                FrameworkParameters = frameworkCfg,
            };


            if (sample.SampleConfiguration != null)
                foreach (var kv in sample.SampleConfiguration.Entries)
                    configuredSample.Parameters[kv.Key] = kv.Value;

            if (sample.MCUConfiguration != null)
                foreach (var kv in sample.MCUConfiguration.Entries)
                    configuredMCU.Configuration[kv.Key] = kv.Value;


            //configuredSample.Parameters["com.sysprogs.examples.ledblink.LEDPORT"] = "GPIOA";
            //configuredSample.Parameters["com.sysprogs.examples.stm32.LEDPORT"] = "GPIOA";
            //configuredSample.Parameters["com.sysprogs.examples.stm32.freertos.heap_size"] = "0";

            var bspDict = configuredMCU.BuildSystemDictionary(new BSPManager());
            bspDict["PROJECTNAME"] = "test";

            if (configuredSample.Frameworks != null)
                foreach (var fw in configuredSample.Frameworks)
                {
                    if (fw.AdditionalSystemVars != null)
                        foreach (var kv in fw.AdditionalSystemVars)
                            bspDict[kv.Key] = kv.Value;
                }

            var prj = new GeneratedProject(mcuDir, configuredMCU, frameworks);
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

            Dictionary<string, bool> sourceExtensions = new Dictionary<string, bool>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var ext in sample.SourceFileExtensions.Split(';'))
                sourceExtensions[ext] = true;

            using (var sw = new StreamWriter(Path.Combine(mcuDir, "Makefile")))
            {
                string prefix = string.Format("{0}\\{1}\\{2}-", mcu.BSP.Toolchain.Directory, mcu.BSP.Toolchain.Toolchain.BinaryDirectory, mcu.BSP.Toolchain.Toolchain.GNUTargetID);
                sw.WriteLine("test.bin: test.elf");
                sw.WriteLine("\t{0}objcopy -O binary $< $@", prefix);
                sw.WriteLine();

                sw.WriteLine("test.elf: {0}", string.Join(" ", prj.SourceFiles.Where(f => sourceExtensions.ContainsKey(Path.GetExtension(f).TrimStart('.'))).Select(f => Path.ChangeExtension(Path.GetFileName(f), ".o"))));
                sw.WriteLine("\t{0}g++ {1} $^ -o $@", prefix, flags.EffectiveLDFLAGS);
                sw.WriteLine();

                foreach (var sf in prj.SourceFiles)
                {
                    string ext = Path.GetExtension(sf);
                    if (!sourceExtensions.ContainsKey(ext.TrimStart('.')))
                        sw.WriteLine($"#{sf} is not a recognized source file");
                    else
                    {
                        bool isCpp = ext.ToLower() != ".c";
                        sw.WriteLine("{0}:", Path.ChangeExtension(Path.GetFileName(sf), ".o"));
                        sw.WriteLine("\t{0}{1} {2} -c {3} -o {4}", prefix, isCpp ? "g++" : "gcc", flags.GetEffectiveCFLAGS(isCpp), sf, Path.ChangeExtension(Path.GetFileName(sf), ".o"));
                        sw.WriteLine();
                    }
                }
            }

            if (!string.IsNullOrEmpty(mcu.MCUDefinitionFile) && sample.ValidateRegisters)
            {
                string firstSrcFileInPrjDir = prj.SourceFiles.First(fn => Path.GetDirectoryName(fn) == mcuDir);
                InsertRegisterValidationCode(firstSrcFileInPrjDir, XmlTools.LoadObject<MCUDefinition>(mcu.MCUDefinitionFile));
            }

            Console.WriteLine("Building {0}...", Path.GetFileName(mcuDir));
            var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + Path.Combine(mcu.BSP.Toolchain.Directory, mcu.BSP.Toolchain.Toolchain.BinaryDirectory, "make.exe") + " -j" + Environment.ProcessorCount + " > build.log 2>&1") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = mcuDir });
            proc.WaitForExit();
            bool success = false;
            string mapFile = Path.Combine(mcuDir, GeneratedProject.MapFileName);
            if (proc.ExitCode == 0 && File.Exists(mapFile))
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

            Directory.Delete(mcuDir, true);
            return TestResult.Succeeded;
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
                }
                _Writer.Dispose();
            }

            internal void BeginSample(string name)
            {
                _Writer.WriteLine("Testing {0}...", name);
                _CurSample = name;
                _ThisTestResults = new Dictionary<string, TestResult>();
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

                foreach (var sample in job.Samples)
                {
                    r.BeginSample(sample.Name);
                    int cnt = 0, failed = 0, succeeded = 0;
                    foreach (var mcu in MCUs)
                    {
                        if (string.IsNullOrEmpty(mcu.ExpandedMCU.ID))
                            throw new Exception("Invalid MCU ID!");

                        string mcuDir = Path.Combine(temporaryDirectory, mcu.ExpandedMCU.ID);
                        var result = TestMCU(mcu, mcuDir + sample.TestDirSuffix, sample);
                        if (result == TestResult.Failed)
                            failed++;
                        else if (result == TestResult.Succeeded)
                            succeeded++;

                        r.LogTestResult(mcu.ExpandedMCU.ID, result);

                        cnt++;
                        Console.WriteLine("{0}: {1}% done ({2}/{3} devices, {4} failed)", sample.Name, (cnt * 100) / MCUs.Length, cnt, MCUs.Length, failed);
                    }

                    if (succeeded == 0)
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

            var toolchain = LoadedToolchain.Load(Environment.ExpandEnvironmentVariables(job.ToolchainPath), new ToolchainRelocationManager());
            var bsp = LoadedBSP.Load(Environment.ExpandEnvironmentVariables(job.BSPPath), toolchain, false);

            TestBSP(job, bsp, args[1]);
        }

        static void InsertRegisterValidationCode(string sourceFile, MCUDefinition mcuDefinition)
        {
            if (!File.Exists(sourceFile))
                throw new Exception("File does not exist: " + sourceFile);

            Regex rgArrayRegister = new Regex("^(sTxMailBox|sFIFOMailBox|sFilterRegister)([0-9]+)_(.*)$");
            Regex rgArrayRegister2 = new Regex("^(IOGXCR|EXTICR|BTCR|BWTR|RAM|FGCLUT|BGCLUT|SDCR|SDTR|DIEPTXF|IT_LINE_SR)([0-9]+)$");
            Regex rgArrayRegister3 = new Regex("^(AFR)(H|L)$");
            Regex rgArrayRegister4 = new Regex("^(HR|CSR)([0-9]+)$");
            Regex rgArrayRegister5 = new Regex("^(TSR|CR)([0-9]+)$");
            Regex rgArrayRegister6 = new Regex("^(TCCR|WPCR|ISR|IER|FIR)([0-9]+)$");

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
                            var m = rgArrayRegister.Match(regName);
                            if (m.Success)
                                regName = string.Format("{0}[{1}].{2}", m.Groups[1], int.Parse(m.Groups[2].ToString()) - 1, m.Groups[3]);
                            else if ((m = rgArrayRegister2.Match(regName)).Success)
                                regName = string.Format("{0}[{1}]", m.Groups[1], int.Parse(m.Groups[2].ToString()) - 1);
                            else if ((m = rgArrayRegister3.Match(regName)).Success)
                                regName = string.Format("{0}[{1}]", m.Groups[1], m.Groups[2].ToString() == "H" ? 1 : 0);
                            else if ((regset.UserFriendlyName == "HASH" || regset.UserFriendlyName == "HASH_DIGEST") && (m = rgArrayRegister4.Match(regName)).Success)
                                regName = string.Format("{0}[{1}]", m.Groups[1], int.Parse(m.Groups[2].ToString()) - 1);
                            else if ((regset.UserFriendlyName == "EXTI" || regset.UserFriendlyName == "EXTI") && (m = rgArrayRegister5.Match(regName)).Success)
                                regName = string.Format("{0}[{1}]", m.Groups[1], int.Parse(m.Groups[2].ToString()) - 1);

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
