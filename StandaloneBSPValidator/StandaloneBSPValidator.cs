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
using static StandaloneBSPValidator.BSPValidator;

namespace StandaloneBSPValidator
{
    public class TestedSample
    {
        public string Name;
        public string TestDirSuffix;
        public string DeviceRegex;
        public bool SkipIfNotFound;
        public bool ValidateRegisters;
        public bool DataSections, NoRTTI;
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
        public string BSPSubfolderMask;
    }

    public class BSPValidator
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

        public enum TestBuildResult
        {
            Succeeded,
            Failed,
            Skipped,
        }

        public struct TestResult
        {
            public TestBuildResult Result;
            public string LogFile;

            public TestResult(TestBuildResult result, string logFile)
            {
                Result = result;
                LogFile = logFile;
            }
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

            internal void AttachDisambiguationSuffix(string suffix)
            {
                int idx = PrimaryOutput.LastIndexOf('.');
                PrimaryOutput = PrimaryOutput.Substring(0, idx) + suffix + PrimaryOutput.Substring(idx);
            }
        }

        class BuildJob
        {
            public List<BuildTask> CompileTasks = new List<BuildTask>();
            public List<BuildTask> OtherTasks = new List<BuildTask>();

            public void GenerateMakeFile(string filePath, string primaryTarget, IEnumerable<string> comments, bool continuePastCompilationErrors)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                using (var sw = new StreamWriter(filePath))
                {
                    if (comments != null)
                    {
                        foreach (var comment in comments)
                            sw.WriteLine("#" + comment);
                        sw.WriteLine();
                    }

                    sw.WriteLine($"all: {primaryTarget}");
                    sw.WriteLine();

                    string modeFlag = continuePastCompilationErrors ? "-" : "";

                    foreach (var task in CompileTasks.Concat(OtherTasks))
                    {
                        sw.WriteLine($"{task.PrimaryOutput}: " + string.Join(" ", task.AllInputs));

                        if ((task.AllInputs?.Sum(x => x.Length) ?? 0) > 5000 && task.Arguments.Contains("$^"))
                        {
                            string rspFile = Path.ChangeExtension(Path.GetFileName(task.PrimaryOutput), ".rsp");
                            File.WriteAllText(Path.Combine(Path.GetDirectoryName(filePath), rspFile), string.Join(" ", task.AllInputs));
                            sw.WriteLine($"\t{modeFlag}{task.Executable} " + task.Arguments.Replace("$^", "@" + rspFile));
                        }
                        else if (task.Arguments.Length > 7000)
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
                            sw.WriteLine($"\t{modeFlag}{task.Executable} {prefixArgs} @{rspFile}");
                        }
                        else
                            sw.WriteLine($"\t{modeFlag}{task.Executable} {task.Arguments}");
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


        public static ValidationJob CreateValidationJobAndUpdateDependencies(LoadedBSP.LoadedMCU mcu,
            VendorSample vs,
            string mcuDir,
            string sampleDirPath,
            bool codeRequiresDebugInfoFlag,
            BSPValidationFlags validationFlags,
            string cmseCacheDirectory)
        {
            if (Directory.Exists(mcuDir))
                Directory.Delete(mcuDir, true);

            var configuredMCU = new LoadedBSP.ConfiguredMCU(mcu, GetDefaultPropertyValues(mcu.ExpandedMCU.ConfigurableProperties));

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
            if (sampleDirPath != null)
                bspDict["SYS:VSAMPLE_DIR"] = sampleDirPath;

            var prj = new GeneratedProject(configuredMCU, vs, mcuDir, bspDict, vs.Configuration.Frameworks ?? new string[0]) { NoRTTI = true };

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
            int idx = flags.CFLAGS.IndexOf("-std=");
            if (!string.IsNullOrEmpty(vs.CLanguageStandard))
            {
                if (idx >= 0)
                {
                    flags.CFLAGS += ' ';
                    flags.CFLAGS = flags.CFLAGS.Remove(idx, flags.CFLAGS.IndexOf(' ', idx) - idx);
                }
                flags.CFLAGS += $" -std={vs.CLanguageStandard}";
            }
            if (!string.IsNullOrEmpty(vs.CPPLanguageStandard))
            {
                if (idx >= 0)
                {
                    flags.CFLAGS += ' ';
                    flags.CFLAGS = flags.CFLAGS.Remove(idx, flags.CFLAGS.IndexOf(' ', idx) - idx);
                }
                flags.CXXFLAGS += $" -std={vs.CPPLanguageStandard}";
            }


            flags.CFLAGS += " -MD";
            flags.CXXFLAGS += " -MD";

            if (codeRequiresDebugInfoFlag)
            {
                flags.CFLAGS += " -ggdb";
                flags.CXXFLAGS += " -ggdb";
            }

            Action successCallback = null;

            if (prj.CMSEImportLibraryName != null)
            {
                flags.LDFLAGS += $" -Wl,--cmse-implib -Wl,--out-implib={prj.CMSEImportLibraryName}";
                Directory.CreateDirectory(cmseCacheDirectory);
                successCallback = () => File.Copy(Path.Combine(mcuDir, prj.CMSEImportLibraryName), Path.Combine(cmseCacheDirectory, vs.InternalUniqueID + "_cmse.o"), true);
            }

            if (prj.RelatedSamples?.Length == 1)
            {
                var cmseLibrary = Path.Combine(cmseCacheDirectory, $"{prj.RelatedSamples[0].ID}_cmse.o");
                if (File.Exists(cmseLibrary))
                {
                    flags.LDFLAGS += " " + cmseLibrary.Replace('\\', '/');
                }
            }

            flags.IncludeDirectories = LoadedBSP.Combine(flags.IncludeDirectories, vs.IncludeDirectories).Distinct().ToArray();
            flags.PreprocessorMacros = LoadedBSP.Combine(flags.PreprocessorMacros, vs.PreprocessorMacros);
            var linkerScriptDirs = (vs.AuxiliaryLinkerScripts ?? new string[0]).Select(ld => Path.GetDirectoryName(ld)).Distinct().Select(dir => VariableHelper.ExpandVariables(dir, bspDict, frameworkCfg)).ToArray();
            flags.AdditionalLibraryDirectories = LoadedBSP.Combine(flags.AdditionalLibraryDirectories, linkerScriptDirs);

            flags.LDFLAGS += " " + vs.LDFLAGS;
            flags = LoadedBSP.ConfiguredMCU.ExpandToolFlags(flags, bspDict, null);

            Dictionary<string, bool> sourceExtensions = new Dictionary<string, bool>(StringComparer.InvariantCultureIgnoreCase);
            sourceExtensions.Add("c", true);
            sourceExtensions.Add("cpp", true);
            sourceExtensions.Add("s", true);

            return new ValidationJob(mcu, mcuDir, prj, flags, sourceExtensions, vs, null, validationFlags, successCallback);
        }

        static void CreateEmptyDirectoryForTestingMCU(string mcuDir)
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
        }

        private static TestResult TestMCU(LoadedBSP.LoadedMCU mcu, string mcuDir, TestedSample sample, DeviceParameterSet extraParameters, RegisterValidationParameters registerValidationParameters)
        {
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
                    return new TestResult(TestBuildResult.Skipped, null);
                }
                else
                    throw new Exception("Cannot find sample: " + sample.Name);
            }

            return TestSingleSample(sampleObj, mcu, mcuDir, sample, extraParameters, registerValidationParameters);
        }

        public static TestResult TestSingleSample(LoadedBSP.LoadedSample sampleObj,
            LoadedBSP.LoadedMCU mcu,
            string testDirectory,
            TestedSample sample,
            DeviceParameterSet extraParameters,
            RegisterValidationParameters registerValidationParameters,
            BSPValidationFlags validationFlags = BSPValidationFlags.None)
        {
            CreateEmptyDirectoryForTestingMCU(testDirectory);

            var configuredMCU = new LoadedBSP.ConfiguredMCU(mcu, GetDefaultPropertyValues(mcu.ExpandedMCU.ConfigurableProperties));
            if (configuredMCU.ExpandedMCU.FLASHSize == 0)
            {
                configuredMCU.Configuration["com.sysprogs.bspoptions.primary_memory"] = "sram";
            }

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

            Dictionary<string, string> mcuDict = null;

            ApplyConfiguration(configuredSample.FrameworkParameters, extraParameters?.FrameworkConfiguration, sample.FrameworkConfiguration);
            ApplyConfiguration(configuredSample.Parameters, extraParameters?.SampleConfiguration, sample.SampleConfiguration);

            foreach (var kv in configuredSample.Parameters.ToArray())
            {
                if (kv.Value.Contains("$$"))
                {
                    if (mcuDict == null)
                    {
                        mcuDict = new Dictionary<string, string>();
                        if (configuredMCU.ExpandedMCU.AdditionalSystemVars != null)
                            foreach (var kv2 in configuredMCU.ExpandedMCU.AdditionalSystemVars)
                                mcuDict[kv2.Key] = kv2.Value;
                    }

                    configuredSample.Parameters[kv.Key] = VariableHelper.ExpandVariables(kv.Value, mcuDict);
                }
            }

            Dictionary<string, bool> frameworkIDs = new Dictionary<string, bool>();
            foreach (var fw in sampleObj.Sample.RequiredFrameworks ?? new string[0])
                frameworkIDs[fw] = true;
            foreach (var fw in sample.AdditionalFrameworks ?? new string[0])
                frameworkIDs[fw] = true;

            var prj = new GeneratedProject(testDirectory, configuredMCU, frameworkIDs.Keys.ToArray()) { DataSections = sample.DataSections, NoRTTI = sample.NoRTTI };
            prj.DoGenerateProjectFromEmbeddedSample(configuredSample, false, bspDict);

            prj.AddBSPFilesToProject(bspDict, configuredSample.FrameworkParameters, frameworkIDs);
            var flags = prj.GetToolFlags(bspDict, configuredSample.FrameworkParameters, frameworkIDs);
            //  if(sampleObj.Sample.LinkerScript!=null)
            //     flags.LinkerScript = sampleObj.Sample.LinkerScript;

            if (!string.IsNullOrEmpty(configuredSample.Sample.Sample.LinkerScript))
                flags.LinkerScript = VariableHelper.ExpandVariables(configuredSample.Sample.Sample.LinkerScript, bspDict, configuredSample.FrameworkParameters);

            if (!string.IsNullOrEmpty(configuredSample.Sample.Sample.CLanguageStandard))
                flags.CFLAGS += $" -std={configuredSample.Sample.Sample.CLanguageStandard}";
            if (!string.IsNullOrEmpty(configuredSample.Sample.Sample.CPPLanguageStandard))
                flags.CXXFLAGS += $" -std={configuredSample.Sample.Sample.CPPLanguageStandard}";

            flags.COMMONFLAGS += " -save-temps ";
            Dictionary<string, bool> sourceExtensions = new Dictionary<string, bool>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var ext in sample.SourceFileExtensions.Split(';'))
                sourceExtensions[ext] = true;

            Console.WriteLine("Building {0}...", Path.GetFileName(testDirectory));
            return new ValidationJob(mcu, testDirectory, prj, flags, sourceExtensions, null, sample.ValidateRegisters ? registerValidationParameters : null, validationFlags).Run();
        }

        public class ValidationJob
        {
            private LoadedBSP.LoadedMCU _MCU;
            private string _Directory;
            private GeneratedProject _Project;
            private ToolFlags _Flags;
            private Dictionary<string, bool> _SourceExtensions;
            private VendorSample _VendorSample;
            private RegisterValidationParameters _RegisterValidationParameters;
            private BSPValidationFlags _ValidationFlags;
            private Action _SuccessCallback;

            internal ValidationJob(LoadedBSP.LoadedMCU mcu, string mcuDir, GeneratedProject prj, ToolFlags flags, Dictionary<string, bool> sourceExtensions, VendorSample vs, RegisterValidationParameters value, BSPValidationFlags validationFlags, Action successCallback = null)
            {
                _MCU = mcu;
                _Directory = mcuDir;
                _Project = prj;
                _Flags = flags;
                _SourceExtensions = sourceExtensions;
                _VendorSample = vs;
                _RegisterValidationParameters = value;
                _ValidationFlags = validationFlags;
                _SuccessCallback = successCallback;
            }

            public CodeScopeSample MakeCodeScopeSample()
            {
                return new CodeScopeSample
                {
                    VendorSample = _VendorSample,
                    SuggestedBuildDirectory = _Directory,
                    SourceFiles = _Project.SourceFiles.Where(f => _SourceExtensions.ContainsKey(Path.GetExtension(f).TrimStart('.'))).ToArray(),
                    Flags = _Flags
                };
            }

            public TestResult Run()
            {
                Directory.CreateDirectory(_Directory);
                BuildJob job = new BuildJob();
                string prefix = string.Format("{0}\\{1}\\{2}", _MCU.BSP.Toolchain.Directory, _MCU.BSP.Toolchain.Toolchain.BinaryDirectory, _MCU.BSP.Toolchain.Toolchain.Prefix);

                foreach (var sf in _Project.SourceFiles)
                {
                    var sfE = sf.Replace('\\', '/');
                    string ext = Path.GetExtension(sf);
                    if (!_SourceExtensions.ContainsKey(ext.TrimStart('.')))
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
                            Arguments = $"-c -o $@ $< {(isCpp ? "-std=gnu++11 " : " ")} {_Flags.GetEffectiveCFLAGS(isCpp, ToolchainSubtype.GCC, ToolFlags.FlagEscapingMode.ForMakefile)}".Replace('\\', '/').Replace("/\"", "\\\""),
                        });
                    }
                }


                List<string> errorLines = new List<string>();
                foreach (var g in job.CompileTasks.GroupBy(t => t.PrimaryOutput.ToLower()))
                {
                    if (g.Count() > 1)
                    {
                        int i = 0;
                        foreach (var j2 in g)
                            j2.AttachDisambiguationSuffix($"_{++i}");

                        errorLines.Add($"ERROR: {g.Key} corresponds to the following files:");
                        foreach (var f in g)
                            errorLines.Add("\t" + f.AllInputs.FirstOrDefault());
                    }
                }

                job.OtherTasks.Add(new BuildTask
                {
                    Executable = prefix + "g++",
                    Arguments = $"{_Flags.StartGroup} {_Flags.EffectiveLDFLAGS} $^ {_Flags.EndGroup} -o $@",
                    AllInputs = job.CompileTasks.Select(t => t.PrimaryOutput)
                        .Concat(_Project.SourceFiles.Where(f => f.EndsWith(".a", StringComparison.InvariantCultureIgnoreCase)).Select(fn => fn.Replace('\\', '/')))
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

                List<string> comments = new List<string>();
                comments.Add("Original directory:" + _VendorSample?.Path);
                comments.Add("Tool flags:");
                comments.Add("\tInclude directories:");
                foreach (var dir in _Flags.IncludeDirectories ?? new string[0])
                    comments.Add("\t\t" + dir);
                comments.Add("\tPreprocessor macros:");
                foreach (var dir in _Flags.PreprocessorMacros ?? new string[0])
                    comments.Add("\t\t" + dir);
                comments.Add("\tLibrary directories:");
                foreach (var dir in _Flags.AdditionalLibraryDirectories ?? new string[0])
                    comments.Add("\t\t" + dir);
                comments.Add("\tLibrary names:");
                foreach (var dir in _Flags.AdditionalLibraries ?? new string[0])
                    comments.Add("\t\t" + dir);
                comments.Add("\tExtra linker inputs:");
                foreach (var dir in _Flags.AdditionalLinkerInputs ?? new string[0])
                    comments.Add("\t\t" + dir);
                comments.Add("\tCFLAGS:" + _Flags.CFLAGS);
                comments.Add("\tCXXFLAGS:" + _Flags.CXXFLAGS);
                comments.Add("\tLDFLAGS:" + _Flags.LDFLAGS);
                comments.Add("\tCOMMONFLAGS:" + _Flags.COMMONFLAGS);

                if (_VendorSample?.Configuration.Frameworks != null)
                {
                    comments.Add("Referenced frameworks:");
                    foreach (var fw in _VendorSample.Configuration.Frameworks)
                        comments.Add("\t" + fw);
                    comments.Add("Framework configuration:");
                    foreach (var kv in _VendorSample.Configuration.Configuration?.Entries ?? new PropertyDictionary2.KeyValue[0])
                        comments.Add($"\t{kv.Key} = {kv.Value}");
                }

                job.GenerateMakeFile(Path.Combine(_Directory, "Makefile"),
                    "test.bin",
                    comments,
                    (_ValidationFlags & BSPValidationFlags.ContinuePastCompilationErrors) != BSPValidationFlags.None);

                if (!string.IsNullOrEmpty(_MCU.MCUDefinitionFile) && _RegisterValidationParameters != null)
                {
                    string firstSrcFileInPrjDir = _Project.SourceFiles.First(fn => Path.GetDirectoryName(fn) == _Directory);
                    InsertRegisterValidationCode(firstSrcFileInPrjDir, XmlTools.LoadObject<MCUDefinition>(_MCU.MCUDefinitionFile), _RegisterValidationParameters);
                }

                if (errorLines.Count > 0 && (_ValidationFlags & BSPValidationFlags.ResolveNameCollisions) == BSPValidationFlags.None)
                {
                    errorLines.Insert(0, "Found critical errors while trying to generate Makefile:");
                    var buildLog = Path.Combine(_Directory, "build.log");
                    File.WriteAllLines(buildLog, errorLines);
                    return new TestResult(TestBuildResult.Failed, buildLog);
                }

                bool buildSucceeded;
                if (true)
                {
                    var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + Path.Combine(_MCU.BSP.Toolchain.Directory, _MCU.BSP.Toolchain.Toolchain.BinaryDirectory, "make.exe") + " -j" + Environment.ProcessorCount + " > build.log 2>&1") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _Directory });
                    proc.WaitForExit();
                    buildSucceeded = proc.ExitCode == 0;
                }
                else
                {
                    buildSucceeded = job.BuildFast(_Directory, Environment.ProcessorCount);
                }


                bool success = false;
                string mapFile = Path.Combine(_Directory, GeneratedProject.MapFileName);
                if (buildSucceeded && File.Exists(mapFile))
                {
                    success = File.ReadAllLines(Path.Combine(_Directory, mapFile)).Where(l => RgMainMap.IsMatch(l)).Count() > 0;

                    if (success)
                    {
                        string binFile = Path.Combine(_Directory, "test.bin");
                        using (var fs = File.Open(binFile, FileMode.Open))
                            if (fs.Length < 512)
                                success = false;

                    }
                }

                var logFile = Path.Combine(_Directory, "build.log");
                if (!success && (_ValidationFlags & BSPValidationFlags.IgnoreLinkerErrors) != 0 && File.Exists(logFile))
                {
                    var text = File.ReadAllText(logFile);
                    if (text.Contains("undefined reference to"))
                        success = true;
                }

                if (!success)
                {
                    if (_VendorSample != null)
                        _VendorSample.AllDependencies = null;
                    return new TestResult(TestBuildResult.Failed, logFile);
                }

                if (_VendorSample != null)
                {
                    _VendorSample.AllDependencies = Directory.GetFiles(_Directory, "*.d")
                        .SelectMany(f => SplitDependencyFile(f).Where(t => !t.EndsWith(":")))
                        .Concat(_Project.SourceFiles.SelectMany(sf => FindIncludedResources(_VendorSample.Path, sf)))
                        .Concat(_VendorSample.AuxiliaryLinkerScripts ?? new string[0])
                        .Distinct()
                        .ToArray();
                }

                _SuccessCallback?.Invoke();
                if ((_ValidationFlags & BSPValidationFlags.KeepDirectoryAfterSuccessfulTest) == BSPValidationFlags.None)
                    Directory.Delete(_Directory, true);

                return new TestResult(TestBuildResult.Succeeded, Path.Combine(_Directory, "build.log"));
            }
        }


        private static IEnumerable<string> FindIncludedResources(string baseDir, string sourceFile)
        {
            List<string> resources = new List<string>();

            const string marker = "\".incbin \\\"";

            foreach (var line in File.ReadLines(sourceFile))
            {
                int idx = line.IndexOf(marker);
                if (idx != -1)
                {
                    idx += marker.Length;
                    int end = line.IndexOf("\\\"", idx);
                    if (end != -1)
                    {
                        //This discovers STM32 binary resources included as '.incbin \"<...>\"'.
                        //The path to the resource is relative to the SW4STM32 project file, that is not directly stored in the VendorSample file.
                        //Hence we use some basic trial-and-error to discover the correct path (assuming that the .cproject file was 0 to 5 levels inside the base dir).
                        //Ideally, we need to store the .cproject file path somewhere in the vendor sample object in order to parse this deterministically.
                        for (int i = 0; i < 5; i++)
                        {
                            string dummySubpath = string.Join("\\", Enumerable.Range(0, i).Select(x => "dummy"));

                            string path = Path.Combine(Path.GetDirectoryName(sourceFile), dummySubpath, line.Substring(idx, end - idx));
                            if (File.Exists(path))
                            {
                                resources.Add(Path.GetFullPath(path));
                                break;
                            }
                        }
                    }
                }
            }

            return resources;
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

        class TestResultLogger : IDisposable
        {
            StreamWriter _Writer;
            string _CurSample;
            Dictionary<string, TestResult> _ThisTestResults = new Dictionary<string, TestResult>();

            Dictionary<string, Dictionary<string, TestResult>> _ResultMap = new Dictionary<string, Dictionary<string, TestResult>>();

            public TestResultLogger(string fn)
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
                    string failed = string.Join(" ", kv.Value.Where(kv2 => kv2.Value.Result == TestBuildResult.Failed).Select(kv2 => kv2.Key));
                    if (failed != "")
                        failed = ", failed on: ";
                    _Writer.WriteLine("{0} succeeded on {1} devices{2}", kv.Key, kv.Value.Where(kv2 => kv2.Value.Result == TestBuildResult.Succeeded).Count(), failed);
                    _Writer.WriteLine("Total test: {0}, failed: {1}", kv.Value.Count(), kv.Value.Where(kv2 => kv2.Value.Result == TestBuildResult.Failed).Count());
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
                _Writer.WriteLine("\t{0}: {1}", mcuID, result.Result);
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

            public int Total => Passed + Failed;
        }

        public static TestStatistics TestBSP(TestJob job, LoadedBSP bsp, string temporaryDirectory, Regex additionalMCUFilter = null)
        {
            TestStatistics stats = new TestStatistics();
            Directory.CreateDirectory(temporaryDirectory);
            using (var r = new TestResultLogger(Path.Combine(temporaryDirectory, "bsptest.log")))
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

                var registerValidationParameters = new RegisterValidationParameters
                {
                    RenameRules = job.RegisterRenamingRules?.Select(rule => new LoadedRenamingRule(rule))?.ToArray(),
                    NonValidatedRegisters = job.NonValidatedRegisters,
                    UndefinedMacros = job.UndefinedMacros
                };

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

                    if (additionalMCUFilter != null)
                        effectiveMCUs = effectiveMCUs.Where(mcu => additionalMCUFilter.IsMatch(mcu.ExpandedMCU.ID)).ToArray();

                    foreach (var mcu in effectiveMCUs)
                    {
                        if (string.IsNullOrEmpty(mcu.ExpandedMCU.ID))
                            throw new Exception("Invalid MCU ID!");

                        var extraParams = job.DeviceParameterSets?.FirstOrDefault(s => s.DeviceRegexObject?.IsMatch(mcu.ExpandedMCU.ID) == true);

                        string mcuDir = Path.Combine(temporaryDirectory, mcu.ExpandedMCU.ID);
                        DateTime start = DateTime.Now;

                        var result = TestMCU(mcu, mcuDir + sample.TestDirSuffix, sample, extraParams, registerValidationParameters);
                        Console.WriteLine($"[{(DateTime.Now - start).TotalMilliseconds:f0} msec]");
                        if (result.Result == TestBuildResult.Failed)
                            failed++;
                        else if (result.Result == TestBuildResult.Succeeded)
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

        public static LoadedBSP LoadBSP(string toolchainID, string bspDir)
        {
            if (string.IsNullOrEmpty(toolchainID))
                toolchainID = (string)Registry.CurrentUser.OpenSubKey(@"Software\Sysprogs\BSPTools\VendorSampleParsers\ToolchainDirectories").GetValue("arm-eabi");

            if (toolchainID.StartsWith("["))
            {
                toolchainID = (string)Registry.CurrentUser.OpenSubKey(@"Software\Sysprogs\GNUToolchains").GetValue(toolchainID.Trim('[', ']'));
                if (toolchainID == null)
                    throw new Exception("Cannot locate toolchain path from registry");
            }

            var toolchain = LoadedToolchain.Load(new ToolchainSource.Other(Environment.ExpandEnvironmentVariables(toolchainID)));
            return LoadedBSP.Load(new BSPEngine.BSPSummary(Path.GetFullPath(Environment.ExpandEnvironmentVariables(bspDir))), toolchain);
        }

        public static void RunJob(string jobFile, string outputDir)
        {
            var job = XmlTools.LoadObject<TestJob>(jobFile);
            job.BSPPath = job.BSPPath.Replace("$$JOBDIR$$", Path.GetDirectoryName(jobFile));

            if (job.BSPSubfolderMask != null)
            {
                foreach (var dir in Directory.GetDirectories(job.BSPPath, job.BSPSubfolderMask))
                {
                    job.BSPPath = dir;
                    TestBSP(job, outputDir);
                }
            }
            else
                TestBSP(job, outputDir);
        }

        static void TestBSP(TestJob job, string outputDir)
        {
            var bsp = LoadBSP(job.ToolchainPath, job.BSPPath);
            TestBSP(job, bsp, outputDir);
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

        public class RegisterValidationParameters
        {
            public LoadedRenamingRule[] RenameRules;
            public string[] NonValidatedRegisters;
            public string[] UndefinedMacros;
        }

        static void InsertRegisterValidationCode(string sourceFile, MCUDefinition mcuDefinition, RegisterValidationParameters parameters)
        {
            if (!File.Exists(sourceFile))
                throw new Exception("File does not exist: " + sourceFile);

            if (mcuDefinition != null)
            {
                var lines = File.ReadAllLines(sourceFile).ToList();
                lines.Insert(0, "#define CPU_IN_SECURE_STATE"); //Some peripheral registers are defined conditionally on this
                lines.Add("");
                lines.Add("#define STATIC_ASSERT(COND) typedef char static_assertion[(COND)?1:-1]");
                lines.Add("void ValidateOffsets()");
                lines.Add("{");
                foreach (var regset in mcuDefinition.RegisterSets)
                    foreach (var reg in regset.Registers)
                    {
                        string regName = reg.Name;
                        if (IsNoValid(regset.UserFriendlyName, parameters.NonValidatedRegisters))
                            continue;
                        if (IsNoValid(regName, parameters.NonValidatedRegisters))
                            continue;
                        if (IsNoValid(regName, parameters.UndefinedMacros))
                            lines.Add($"#undef {regName}");
                        if (parameters.RenameRules != null)
                            foreach (var rule in parameters.RenameRules)
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
                            lines.Add(string.Format("STATIC_ASSERT((unsigned)&({0}->r{1}) == {2});", regset.UserFriendlyName, regName, reg.Address));
                        }
                        else
                            lines.Add(string.Format("STATIC_ASSERT((unsigned)&({0}->{1}) == {2});", regset.UserFriendlyName, regName, reg.Address));
                    }
                lines.Add("}");
                File.WriteAllLines(sourceFile, lines.ToArray());
            }
        }
    }

    [Flags]
    public enum BSPValidationFlags
    {
        None = 0,
        KeepDirectoryAfterSuccessfulTest = 1,

        //Enabling this flag will automatically resolve the condition where multiple sources with the same name exist in different directories.
        //This can be enabled for Pass 1 tests (in-place build) only if the conflicts will get resolved by attaching embedded frameworks that have the files renamed (e.g. STM32MP1).
        //!!!DO NOT ENABLE THIS OPTION FOR PASS 2 TESTS!!! MSBuild and Make backends to not support colliding source file names, so projects containing them will not build.
        ResolveNameCollisions = 2,

        ContinuePastCompilationErrors = 4,
        IgnoreLinkerErrors = 8,
    }

    public static class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
                throw new Exception("Usage: StandaloneBSPValidator <job file> <output dir>");

            BSPValidator.RunJob(args[0], args[1]);
        }

    }


}
