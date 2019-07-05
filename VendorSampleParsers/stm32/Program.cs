using BSPEngine;
using BSPGenerationTools;
using stm32_bsp_generator;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using VendorSampleParserEngine;

namespace GeneratorSampleStm32
{

    class Program
    {
        static public List<string> ToAbsolutePath(string dir, string topLevelDir, List<string> lstDir)
        {
            List<string> srcAbc = new List<string>();

            foreach (var sf in lstDir)
            {
                string fn = sf.Trim(' ');
                fn = fn.Replace(@"/RVDS/", @"/GCC/");
                fn = fn.Replace(@"\RVDS\", @"\GCC\");

                if (!Path.IsPathRooted(fn))
                    fn = Path.GetFullPath(Path.Combine(dir, fn));

                if (!File.Exists(fn) && !Directory.Exists(fn))
                {
                    if (Path.GetFileName(fn).ToLower() == "readme.txt" || Path.GetFileName(fn).ToLower() == "ipv4")
                        continue;

                    if (fn.EndsWith("\\component", StringComparison.InvariantCultureIgnoreCase) && Directory.Exists(fn + "s"))
                        fn += "s";
                    else
                    {
                        string fn2 = Path.Combine(topLevelDir, sf);
                        if (Directory.Exists(fn2))
                            fn = fn2;
                        else
                            Console.WriteLine("Missing file/directory: " + fn);
                    }
                }

                srcAbc.Add(fn);
            }
            return srcAbc;
        }

        static Regex rgExamplesSuffix = new Regex(@"\\Examples(_[^\\]+)\\", RegexOptions.IgnoreCase);
        static Regex rgExamplesSuffix2 = new Regex(@"\\Applications\\USB(_Host|_Device)\\", RegexOptions.IgnoreCase);

        static void AppendSamplePrefixFromPath(ref string sampleName, string dir)  //Otherwise we get ambiguous sample IDs
        {
            Match m;
            if (sampleName == "Master" || sampleName == "Slave" || sampleName == "FreeRTOS" || sampleName == "LedToggling" || sampleName == "HelloWorld")
                sampleName = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(dir))) + "_" + sampleName;
            if ((m = rgExamplesSuffix.Match(dir)).Success)
                sampleName += m.Groups[1].Value;
            else if ((m = rgExamplesSuffix2.Match(dir)).Success)
                sampleName += m.Groups[1].Value;
        }

        static public List<VendorSample> GetInfoProjectFromMDK(string pDirPrj, string topLevelDir, string boardName, List<string> extraIncludeDirs)
        {
            List<VendorSample> aLstVSampleOut = new List<VendorSample>();
            int aCntTarget = 0;
            string aFilePrj = Directory.GetFiles(pDirPrj, "*.uvprojx")[0];

            string aNamePrj = Path.GetFileName(Path.GetDirectoryName(pDirPrj));

            List<string> sourceFiles = new List<string>();
            List<string> includeDirs = new List<string>();
            bool flGetProperty = false;
            string aTarget = "";
            VendorSample sample = new VendorSample();

            foreach (var ln in File.ReadAllLines(aFilePrj))
            {
                if (ln.Contains("<Target>"))
                {
                    if (aCntTarget == 0)
                        sample = new VendorSample();
                    aCntTarget++;
                }
                if (ln.Contains("</Target>"))
                    if (aCntTarget == 0)
                        throw new Exception("wrong tag Targets");
                    else
                        aCntTarget--;

                if (ln.Contains("<Cads>"))
                    flGetProperty = true;
                else if (ln.Contains("</Cads>"))
                    flGetProperty = false;

                Match m = Regex.Match(ln, "[ \t]*<Device>(.*)</Device>[ \t]*");
                if (m.Success)
                {
                    sample.DeviceID = m.Groups[1].Value;
                    if (sample.DeviceID.EndsWith("x"))
                        sample.DeviceID = sample.DeviceID.Remove(sample.DeviceID.Length - 2, 2);
                }
                m = Regex.Match(ln, "[ \t]*<TargetName>(.*)</TargetName>[ \t]*");
                if (m.Success)
                    aTarget = m.Groups[1].Value;

                MatchCollection m1 = Regex.Matches(ln, @"[ ]*<FilePath>([\w\-:\\./]*)</FilePath>[ ]*");
                foreach (Match mc in m1)
                {
                    string filePath = mc.Groups[1].Value;
                    if (filePath.StartsWith(@"./") || filePath.StartsWith(@".\"))
                        filePath = pDirPrj + filePath.Substring(1);
                    if (filePath.EndsWith(".s", StringComparison.InvariantCultureIgnoreCase))
                        continue;

                    if (filePath.EndsWith(".lib", StringComparison.InvariantCultureIgnoreCase))
                    {
                        filePath = filePath.Replace("_Keil_wc16.lib", "_GCC_wc32.a");
                        filePath = filePath.Replace("_Keil.lib", "_GCC.a");
                        filePath = filePath.Replace("_Keil_ARGB.lib", "_GCC_ARGB.a");
                        filePath = filePath.Replace(@"Keil\touchgfx_core.lib", @"gcc\libtouchgfx-float-abi-hard.a");
                        if (!File.Exists(Path.Combine(pDirPrj, filePath)))
                            continue;
                    }
                    if (filePath.EndsWith(".a", StringComparison.InvariantCultureIgnoreCase) && filePath.IndexOf("_IAR", StringComparison.InvariantCultureIgnoreCase) != -1)
                    {
                        filePath = filePath.Replace("_IAR_ARGB.a", "_GCC_ARGB.a");
                        if (!File.Exists(Path.Combine(pDirPrj, filePath)))
                            continue;
                    }

                    if (filePath.EndsWith(".a", StringComparison.InvariantCultureIgnoreCase))
                    {
                        filePath = filePath.Replace("_wc16", "_wc32");
                    }

                    if (!sourceFiles.Contains(filePath))
                        sourceFiles.Add(filePath);
                }

                if (flGetProperty)
                {
                    m = Regex.Match(ln, "[ \t]*<IncludePath>(.*)</IncludePath>[ \t]*");
                    if (m.Success && m.Groups[1].Value != "")
                        sample.IncludeDirectories = m.Groups[1].Value.Split(';').Select(FixIncludePath).ToArray();

                    m = Regex.Match(ln, "[ \t]*<Define>(.*)</Define>[ \t]*");
                    if (m.Success && m.Groups[1].Value != "")
                    {
                        sample.PreprocessorMacros = m.Groups[1].Value.Replace("&gt;", ">").Replace("&lt;", "<").Split(',').Select(t => t.Trim()).Where(t => t != "").ToArray();

                        for (int i = 0; i < sample.PreprocessorMacros.Length; i++)
                            if (sample.PreprocessorMacros[i].Contains("\"<"))
                            {
                                //This is likely the MBEDTLS_CONFIG_FILE="<mbedtls_config.h>" macro. We need to remove the quotes, as VisualGDB will automatically escape it anyway.
                                sample.PreprocessorMacros[i] = sample.PreprocessorMacros[i].Replace("\"<", "<").Replace(">\"", ">");
                            }
                    }
                }


                if (ln.Contains("</Target>") && aCntTarget == 0)
                {
                    sample.Path = Path.GetDirectoryName(pDirPrj);
                    sample.UserFriendlyName = aNamePrj;
                    AppendSamplePrefixFromPath(ref sample.UserFriendlyName, pDirPrj);

                    sample.BoardName = aTarget;
                    sample.SourceFiles = ToAbsolutePath(pDirPrj, topLevelDir, sourceFiles).ToArray();

                    foreach (var fl in sample.IncludeDirectories)
                        includeDirs.Add(fl);
                    includeDirs.AddRange(extraIncludeDirs);
                    sample.IncludeDirectories = ToAbsolutePath(pDirPrj, topLevelDir, includeDirs).ToArray();

                    string readmeFile = Path.Combine(pDirPrj, @"..\readme.txt");
                    if (File.Exists(readmeFile))
                    {
                        string readmeContents = File.ReadAllText(readmeFile);
                        Regex rgTitle = new Regex(@"@page[ \t]+[^ \t]+[ \t]+([^ \t][^@]*)\r\n *\r\n", RegexOptions.Singleline);
                        m = rgTitle.Match(readmeContents);
                        if (m.Success)
                        {
                            sample.Description = m.Groups[1].Value;
                        }
                    }

                    var fsdataCustom = sample.SourceFiles.FirstOrDefault(f => Path.GetFileName(f).ToLower() == "fsdata_custom.c");
                    if (fsdataCustom != null)
                    {
                        var lines = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(fsdataCustom), "fs.c"));
                        if (lines.Contains("#include \"fsdata.c\""))
                            sample.SourceFiles = sample.SourceFiles.Except(new[] { fsdataCustom }).ToArray();
                        else
                        {
                            //Should not happen
                            Debug.Assert(false);
                        }
                    }

                    var syscallsFile = Path.Combine(sample.Path, "SW4STM32", "syscalls.c");
                    if (File.Exists(syscallsFile))
                    {
                        //This file is included in some samples to handle I/O redirection. Since it is GCC-specific, it's not referenced by the Keil project file we are parsing.
                        sample.SourceFiles = sample.SourceFiles.Concat(new[] { syscallsFile }).ToArray();
                    }

                    aLstVSampleOut.Add(sample);
                }
            }


            if (aLstVSampleOut.Count == 1)
                aLstVSampleOut[0].BoardName = boardName;    //In some example projects, the target name is defined incorrectly
            else
            {
                foreach (var s in aLstVSampleOut)
                    s.BoardName = boardName + "-" + s.BoardName;
            }

            return aLstVSampleOut;
        }

        private static string FixIncludePath(string path)
        {
            path = path.TrimEnd('/', '\\');
            if (path.StartsWith("/../"))
                path = path.Substring(1);
            return path;
        }

        static string ExtractFirstSubdir(string dir) => dir.Split('\\')[1];

        class STM32SampleRelocator : VendorSampleRelocator
        {
            private ConstructedVendorSampleDirectory _Directory;

            public STM32SampleRelocator(ConstructedVendorSampleDirectory dir, ReverseConditionTable optionalConditionTableForFrameworkMapping)
                : base(optionalConditionTableForFrameworkMapping)
            {
                _Directory = dir;
                /*
                    Known problems with trying to map frameworks:
                      HAL:
                        * Much longer build times
                        * LL-only samples don't provide cfg files for HAL
                        * HAL-only samples don't provide stm32_assert.h needed by LL
                      lwIP:
                        * Different SDKs have slightly different file layouts
                        * Some samples don't provide sys_xxx() functions
                */

                AutoDetectedFrameworks = new AutoDetectedFramework[]
                {/*
                    new AutoDetectedFramework {FrameworkID = "com.sysprogs.arm.stm32.hal",
                        FileRegex = new Regex(@"\$\$SYS:VSAMPLE_DIR\$\$/[^/\\]+/Drivers/[^/\\]+_HAL_Driver", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                        DisableTriggerRegex = new Regex(@"_ll_[^/\\]+\.c", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                        Configuration = new Dictionary<string, string>() }
                        */

                    /*new AutoDetectedFramework {FrameworkID = "com.sysprogs.arm.stm32.LwIP",
                        FileRegex = new Regex(@"\$\$SYS:VSAMPLE_DIR\$\$/[^/\\]+/Middlewares/Third_Party/LwIP", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                        DisableTriggerRegex = new Regex(@"^$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                        Configuration = new Dictionary<string, string>() }*/
                };

                AutoPathMappings = new PathMapping[]
                {
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/([^_]+)/Drivers/STM32[^/\\]+xx_HAL_Driver/(.*)", "$$SYS:BSP_ROOT$$/STM32{1}xxxx/STM32{1}xx_HAL_Driver/{2}"),
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/([^_]+)/Drivers/CMSIS/(.*)", "$$SYS:BSP_ROOT$$/STM32{1}xxxx/CMSIS_HAL/{2}"),

                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/([^_]+)/Middlewares/ST/STM32_USB_(Host|Device)_Library/(.*)", "$$SYS:BSP_ROOT$$/STM32{1}xxxx/STM32_USB_{2}_Library/{3}"),
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/([^_]+)/Middlewares/Third_Party/(FreeRTOS)/(.*)", "$$SYS:BSP_ROOT$$/{2}/{3}"),

                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/WB/Middlewares/ST/STM32_WPAN(.*)", "$$SYS:BSP_ROOT$$/STM32WBxxxx/STM32_WPAN{1}"),
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/WB/Drivers/BSP/(.*)", "$$SYS:BSP_ROOT$$/STM32WBxxxx/BSP/{1}"),
                };
            }

            public override Dictionary<string, string> InsertVendorSamplesIntoBSP(ConstructedVendorSampleDirectory dir, VendorSample[] sampleList, string bspDirectory)
            {
                var copiedFiles = base.InsertVendorSamplesIntoBSP(dir, sampleList, bspDirectory);

                Regex rgDebugger = new Regex("#define[ \t]+CFG_DEBUGGER_SUPPORTED[ \t]+(0)$");

                foreach (var kv in copiedFiles)
                {
                    if (kv.Value.EndsWith("app_conf.h", StringComparison.InvariantCultureIgnoreCase))
                    {
                        var lines = File.ReadAllLines(kv.Value);
                        bool modified = false;
                        for (int i = 0; i < lines.Length; i++)
                        {
                            var m = rgDebugger.Match(lines[i]);
                            if (m.Success)
                            {
                                lines[i] = lines[i].Substring(0, m.Groups[1].Index) + "1";
                                modified = true;
                            }
                        }

                        if (modified)
                            File.WriteAllLines(kv.Value, lines);
                    }
                }

                return copiedFiles;
            }

            protected override void FilterPreprocessorMacros(ref string[] macros)
            {
                base.FilterPreprocessorMacros(ref macros);
                macros = macros.Where(m => !m.StartsWith("STM32") || m.Contains("_")).Concat(new string[] { "$$com.sysprogs.stm32.hal_device_family$$" }).ToArray();
            }

            protected override string BuildVirtualSamplePath(string originalPath)
            {
                return string.Join("\\", originalPath.Split('/').Skip(2).Reverse().Skip(1).Reverse());
            }

            protected override PathMapper CreatePathMapper(ConstructedVendorSampleDirectory dir) => new STM32PathMapper(_Directory);
        }

        class STM32PathMapper : VendorSampleRelocator.PathMapper
        {
            public STM32PathMapper(ConstructedVendorSampleDirectory dir)
                : base(dir)
            {
            }

            const string Prefix1 = @"C:/QuickStep/STM32Cube_FW_H7_clean/Firmware";

            Regex rgFWFolder = new Regex(@"^(\$\$SYS:VSAMPLE_DIR\$\$)/[^/]+/STM32Cube_FW_([^_]+)_V[^/]+/(.*)$", RegexOptions.IgnoreCase);

            public override string MapPath(string path)
            {
                if (path?.StartsWith(Prefix1) == true)
                {
                    path = $@"{_SampleDir.SourceDirectory}/H7_1.4.0/STM32Cube_FW_H7_V1.4.0/{path.Substring(Prefix1.Length)}";
                    if (!File.Exists(path) && !Directory.Exists(path))
                        throw new Exception("Missing " + path);
                }

                string result = base.MapPath(path);
                if (result?.StartsWith("$$SYS:VSAMPLE_DIR$$/") == true)
                {
                    var m = rgFWFolder.Match(result);
                    if (!m.Success)
                        throw new Exception("Unexpected path format: " + result);

                    result = $"{m.Groups[1]}/{m.Groups[2]}/{m.Groups[3]}";
                }

                result = result?.Replace("/SW4STM32/", "/");
                if (result?.EndsWith(".ld") == true)
                {
                    int idx = result.LastIndexOf('/');
                    int idx2 = result.LastIndexOf('/', idx - 1);
                    //Some linker script files have too long paths. Shorten them by moving them one step up.
                    result = result.Substring(0, idx2) + result.Substring(idx);
                }

                return result;
            }
        }

        class STM32VendorSampleParser : VendorSampleParser
        {
            public STM32VendorSampleParser(string ruleset)
                : base(@"..\..\generators\stm32\output\" + ruleset, "STM32 CubeMX Samples", ruleset)
            {
            }

            protected override VendorSampleRelocator CreateRelocator(ConstructedVendorSampleDirectory sampleDir)
            {
                ReverseConditionTable table = null;
                var conditionTableFile = Path.Combine(BSPDirectory, ReverseFileConditionBuilder.ReverseConditionListFileName + ".gz");
                if (File.Exists(conditionTableFile))
                    table = XmlTools.LoadObject<ReverseConditionTable>(conditionTableFile);

                return new STM32SampleRelocator(sampleDir, table);
            }

            static bool IsNonGCCFile(VendorSample vs, string fn)
            {
                if (fn.StartsWith(vs.Path + @"\MDK-ARM", StringComparison.InvariantCultureIgnoreCase))
                    return true;

                return false;
            }

            protected override void AdjustVendorSampleProperties(VendorSample vs)
            {
                base.AdjustVendorSampleProperties(vs);
                vs.SourceFiles = vs.SourceFiles.Where(s => !IsNonGCCFile(vs, s)).ToArray();
            }


            protected override ParsedVendorSamples ParseVendorSamples(string SDKdir, IVendorSampleFilter filter)
            {
                var SDKs = XmlTools.LoadObject<STM32SDKCollection>(Path.Combine(BSPDirectory, "SDKVersions.xml"));

                List<VendorSample> allSamples = new List<VendorSample>();

                foreach (var sdk in SDKs.SDKs)
                {
                    List<string> addInc = new List<string>();
                    string topLevelDir = Directory.GetDirectories(Path.Combine(SDKdir, sdk.FolderName), "STM32Cube_*")[0];

                    addInc.Add($@"{topLevelDir}\Drivers\CMSIS\Include");

                    int sampleCount = 0;
                    Console.WriteLine($"Discovering samples for {sdk.Family}...");

                    foreach (var boardDir in Directory.GetDirectories(Path.Combine(topLevelDir, "Projects")))
                    {
                        string boardName = Path.GetFileName(boardDir);

                        foreach (var dir in Directory.GetDirectories(boardDir, "Mdk-arm", SearchOption.AllDirectories))
                        {
                            string sampleName = Path.GetFileName(Path.GetDirectoryName(dir));
                            AppendSamplePrefixFromPath(ref sampleName, dir);

                            if (!filter.ShouldParseSampleForAnyDevice(sampleName))
                                continue;   //We are only reparsing a subset of samples

                            var aSamples = GetInfoProjectFromMDK(dir, topLevelDir, boardName, addInc);

                            if (aSamples.Count != 0)
                                Debug.Assert(aSamples[0].UserFriendlyName == sampleName);   //Otherwise quick reparsing won't work.

                            var scriptDir = Path.Combine(dir, "..", "SW4STM32");
                            if (Directory.Exists(scriptDir))
                            {
                                string[] linkerScripts = Directory.GetFiles(scriptDir, "*.ld", SearchOption.AllDirectories);
                                if (linkerScripts.Length == 1)
                                {
                                    foreach (var sample in aSamples)
                                        sample.LinkerScript = Path.GetFullPath(linkerScripts[0]);
                                }
                                else
                                {
                                    //Some sample projects define multiple linker scripts (e.g. STM32F072RBTx_FLASH.ld vs. STM32F072VBTx_FLASH.ld).
                                    //In this case we don't pick the sample-specific linker script and instead go with the regular BSP script for the selected MCU.s
                                }
                            }

                            sampleCount += aSamples.Count;
                            allSamples.AddRange(aSamples);
                        }
                    }

                    Console.WriteLine($"Found {sampleCount} samples for {sdk.Family}.");
                }

                return new ParsedVendorSamples { VendorSamples = allSamples.ToArray() };
            }
        }

        static void Main(string[] args)
        {
            List<string> regularArgs = new List<string>();
            string ruleset = "classic";
            foreach (var arg in args)
            {
                if (arg.StartsWith("/rules:"))
                    ruleset = arg.Substring(7);
                else
                    regularArgs.Add(arg);
            }

            new STM32VendorSampleParser(ruleset).Run(regularArgs.ToArray());
        }
    }
}
