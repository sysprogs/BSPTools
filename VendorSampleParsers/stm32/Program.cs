using BSPEngine;
using BSPGenerationTools;
using GeneratorSampleStm32.ProjectParsers;
using StandaloneBSPValidator;
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
        static public List<string> ToAbsolutePaths(string dir, string topLevelDir, List<string> relativePaths)
        {
            List<string> srcAbc = new List<string>();

            foreach (var sf in relativePaths)
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
                        {
                            Console.WriteLine("Missing file/directory: " + fn);
                            continue;
                        }
                    }
                }

                srcAbc.Add(fn);
            }
            return srcAbc;
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
                    //AppendSamplePrefixFromPath(ref sample.UserFriendlyName, pDirPrj);

                    sample.BoardName = aTarget;
                    sample.SourceFiles = ToAbsolutePaths(pDirPrj, topLevelDir, sourceFiles).ToArray();

                    foreach (var fl in sample.IncludeDirectories)
                        includeDirs.Add(fl);
                    includeDirs.AddRange(extraIncludeDirs);
                    sample.IncludeDirectories = ToAbsolutePaths(pDirPrj, topLevelDir, includeDirs).ToArray();

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
            private STM32Ruleset _Ruleset;

            public STM32SampleRelocator(ConstructedVendorSampleDirectory dir, ReverseConditionTable optionalConditionTableForFrameworkMapping, STM32Ruleset ruleset)
                : base(optionalConditionTableForFrameworkMapping)
            {
                _Directory = dir;
                _Ruleset = ruleset;
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

                    new AutoDetectedFramework {FrameworkID = "com.sysprogs.arm.stm32.threadx",
                        FileRegex = new Regex(@"\$\$SYS:VSAMPLE_DIR\$\$/[^/\\]+/Middlewares/ST/threadx/(common|ports)/.*", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                        DisableTriggerRegex = new Regex(@"\$\$SYS:VSAMPLE_DIR\$\$/[^/\\]+/Middlewares/ST/threadx/ports/.*", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                        Configuration = new Dictionary<string, string>{ { "com.sysprogs.bspoptions.stm32.threadx.user_define", "TX_INCLUDE_USER_DEFINE_FILE"} },
                        SkipFrameworkRegex = new Regex(@"\$\$SYS:VSAMPLE_DIR\$\$/[^/\\]+/Middlewares/ST/threadx/common_modules/.*", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                        FileBasedConfig = new[]
                        {
                            new FileBasedConfigEntry(@"threadx/ports/cortex[^/]+/gnu/src/tx_thread_secure_stack.c", "com.sysprogs.bspoptions.stm32.threadx.secure_domain"),
                        }
                    },

                    new AutoDetectedFramework {FrameworkID = "com.sysprogs.arm.stm32.filex",
                        FileRegex = new Regex(@"\$\$SYS:VSAMPLE_DIR\$\$/[^/\\]+/Middlewares/ST/filex/.*", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                        FileBasedConfig = new[]
                        {
                            new FileBasedConfigEntry(@"filex/common/drivers/fx_stm32_(.*)_driver\.c", "com.sysprogs.bspoptions.stm32.filex.{1}")
                        }
                    },

                    new AutoDetectedFramework {FrameworkID = "com.sysprogs.arm.stm32.levelx",
                        FileRegex = new Regex(@"\$\$SYS:VSAMPLE_DIR\$\$/[^/\\]+/Middlewares/ST/levelx/.*", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                        FileBasedConfig = new[]
                        {
                            new FileBasedConfigEntry(@"levelx/common/drivers/lx_stm32_(.*)_driver\.c", "com.sysprogs.bspoptions.stm32.levelx.{1}")
                        }
                    },

                    new AutoDetectedFramework {FrameworkID = "com.sysprogs.arm.stm32.usbx",
                        FileRegex = new Regex(@"\$\$SYS:VSAMPLE_DIR\$\$/[^/\\]+/Middlewares/ST/usbx/.*", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                        UnsupportedDeviceRegex = new Regex("STM32(G4|L5).*"),    //The HAL for these families does not have the HAL_PCD_EP_Abort() call and requires a slightly different USBX port
                        FileBasedConfig = new[]
                        {
                            new FileBasedConfigEntry(@"usbx/common/usbx(|_stm32)_(host_controllers|device_controllers)/.*", "com.sysprogs.bspoptions.stm32.usbx.{2}"),
                            new FileBasedConfigEntry(@"usbx/common/usbx_device_classes/src/ux_device_class_(audio|ccid|cdc_acm|cdc_ecm|dfu|hid|pima|printer|rndis|storage|video)_.*", "com.sysprogs.bspoptions.stm32.usbx.device_class_{1}"),
                            new FileBasedConfigEntry(@"usbx/common/usbx_host_classes/src/ux_host_class_(asix|audio|cdc_acm|cdc_ecm|gser|hid|hub|pima|printer|prolific|storage|swar|video)_.*", "com.sysprogs.bspoptions.stm32.usbx.host_class_{1}"),
                            new FileBasedConfigEntry(@"usbx/common/usbx_(network|pictbridge)/.*", "com.sysprogs.bspoptions.stm32.usbx.{1}"),
                        }
                    },

                    new AutoDetectedFramework {FrameworkID = "com.sysprogs.arm.stm32.extmem.manager",
                        FileRegex = new Regex(@"\$\$SYS:BSP_ROOT\$\$/STM32_ExtMem_Manager/.*", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                        FileBasedConfig = new[]
                        {
                            new FileBasedConfigEntry(@"/stm32_boot_lrun\.c", "com.sysprogs.bspoptions.stm32.extmem.manager.lrun")
                        }
                    },                    
                    
                    new AutoDetectedFramework {FrameworkID = "com.sysprogs.arm.stm32.bspdrv.lan8742",
                        FileRegex = new Regex(@"\$\$SYS:BSP_ROOT\$\$/STM32[^/]+/BSP/Components/lan8742/lan8742.c", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                    },
                };


                AutoPathMappings = new PathMapping[]
                {
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/([^_]+)/Drivers/STM32[^/\\]+xx_HAL_Driver/(.*)", "$$SYS:BSP_ROOT$$/STM32{1}xxxx/STM32{1}xx_HAL_Driver/{2}"),
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/([^_]+)/Drivers/CMSIS/DSP/(.*)", "$$SYS:BSP_ROOT$$/STM32{1}xxxx/DSP/{2}"),
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/([^_]+)/Drivers/CMSIS/(.*)", "$$SYS:BSP_ROOT$$/STM32{1}xxxx/CMSIS_HAL/{2}"),

                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/([^_]+)/Middlewares/ST/STM32_USB_(Host|Device)_Library/(.*)", "$$SYS:BSP_ROOT$$/STM32{1}xxxx/STM32_USB_{2}_Library/{3}"),
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/([^_]+)/Middlewares/Third_Party/(FreeRTOS)/(.*)", "$$SYS:BSP_ROOT$$/{2}/{3}"),

                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/WB/Middlewares/ST/STM32_WPAN(.*)", "$$SYS:BSP_ROOT$$/STM32WBxxxx/STM32_WPAN{1}"),
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/(WB)/Drivers/BSP/(.*)", "$$SYS:BSP_ROOT$$/STM32{1}xxxx/BSP/{2}"),
                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/MP1/Middlewares/Third_Party/OpenAMP/(.*)", "$$SYS:BSP_ROOT$$/OpenAMP/{1}"),

                    new PathMapping(@"\$\$SYS:VSAMPLE_DIR\$\$/(F4|F7|H7)/Drivers/BSP/(.*)", "$$SYS:BSP_ROOT$$/STM32{1}xxxx/BSP/{2}"),
                };
            }

            public override Dictionary<string, string> InsertVendorSamplesIntoBSP(ConstructedVendorSampleDirectory dir, VendorSample[] sampleList, string bspDirectory, BSPReportWriter reportWriter, bool cleanCopy)
            {
                var copiedFilesByTarget = base.InsertVendorSamplesIntoBSP(dir, sampleList, bspDirectory, reportWriter, cleanCopy);

                Regex rgDebugger = new Regex("#define[ \t]+CFG_DEBUGGER_SUPPORTED[ \t]+(0)$");

                foreach (var kv in copiedFilesByTarget)
                {
                    if (kv.Key.EndsWith("app_conf.h", StringComparison.InvariantCultureIgnoreCase))
                    {
                        var lines = File.ReadAllLines(kv.Key);
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
                            File.WriteAllLines(kv.Key, lines);
                    }
                }

                return copiedFilesByTarget;
            }

            protected override void FilterPreprocessorMacros(ref string[] macros)
            {
                base.FilterPreprocessorMacros(ref macros);

                if (_Ruleset == STM32Ruleset.BlueNRG_LP)
                    macros = macros.Where(m => m != "CONFIG_DEVICE_BLUENRG_LP").ToArray();
                else
                    macros = macros.Where(m => !m.StartsWith("STM32") || m.Contains("_")).Concat(new string[] { "$$com.sysprogs.stm32.hal_device_family$$" }).ToArray();
            }

            protected override string BuildVirtualSamplePath(string originalPath)
            {
                return string.Join("\\", originalPath.Split('/').Skip(2).Reverse().Skip(1).Reverse());
            }

            protected override PathMapper CreatePathMapper(ConstructedVendorSampleDirectory dir) => new STM32PathMapper(_Directory, _Ruleset);
        }

        class STM32PathMapper : VendorSampleRelocator.PathMapper
        {
            public STM32PathMapper(ConstructedVendorSampleDirectory dir, STM32Ruleset ruleset)
                : base(dir)
            {
                _Ruleset = ruleset;
            }

            const string Prefix1 = @"C:/QuickStep/STM32Cube_FW_H7_clean/Firmware";

            Regex rgFWFolder = new Regex(@"^(\$\$SYS:VSAMPLE_DIR\$\$)/[^/]+/STM32Cube_FW_([^_]+)_V[^/]+/(.*)$", RegexOptions.IgnoreCase);
            private STM32Ruleset _Ruleset;

            string DoMapPath(string path)
            {
                if (path?.StartsWith(Prefix1) == true)
                {
                    path = $@"{_SampleDir.SourceDirectory}/H7_1.4.0/STM32Cube_FW_H7_V1.4.0/{path.Substring(Prefix1.Length)}";
                    if (!File.Exists(path) && !Directory.Exists(path))
                        throw new Exception("Missing " + path);
                }

                string result = base.MapPath(path);
                if (_Ruleset == STM32Ruleset.BlueNRG_LP)
                {

                }
                else if (result?.StartsWith("$$SYS:VSAMPLE_DIR$$/") == true)
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

            Dictionary<string, string> _DirectMappingDict = new Dictionary<string, string>();
            Dictionary<string, string> _ReverseMappingDict = new Dictionary<string, string>();

            public override string MapPath(string path)
            {
                if (path == null)
                    return null;

                var fullPath = Path.GetFullPath(path);
                if (_DirectMappingDict.TryGetValue(fullPath, out var result))
                    return result;

                result = DoMapPath(path);

                if (result == null)
                    return result;

                if (_ReverseMappingDict.TryGetValue(result, out var oldPath) && oldPath != fullPath && !Enumerable.SequenceEqual(File.ReadAllBytes(oldPath), File.ReadAllBytes(fullPath)))
                {
                    int idx = result.LastIndexOf('/');
                    if (idx == -1)
                        throw new Exception("Invalid mapped path: " + result);

                    int idx2 = result.LastIndexOf('.');
                    string pathBase, ext;
                    if (idx2 > idx)
                    {
                        pathBase = result.Substring(0, idx2);
                        ext = result.Substring(idx2);
                    }
                    else
                    {
                        pathBase = result;
                        ext = "";
                    }

                    for (int i = 2; ; i++)
                    {
                        string candidate = $"{pathBase}v{i}{ext}";
                        if (!_ReverseMappingDict.ContainsKey(candidate))
                        {
                            result = candidate;
                            break;
                        }
                    }
                }

                _ReverseMappingDict[result] = fullPath;
                _DirectMappingDict[fullPath] = result;

                return result;
            }
        }

        class STM32VendorSampleParser : VendorSampleParser
        {
            public STM32VendorSampleParser(string ruleset)
                : base(@"..\..\generators\stm32\output\" + ruleset, (ruleset == "bluenrg-lp") ? "BlueNRG SDK Samples" : "STM32 CubeMX Samples", ruleset)
            {
                _Ruleset = (STM32Ruleset)Enum.Parse(typeof(STM32Ruleset), ruleset, true);
                _ForceCSemanticsForCodeScope = true;
            }

            STM32Ruleset _Ruleset;

            protected override VendorSampleRelocator CreateRelocator(ConstructedVendorSampleDirectory sampleDir)
            {
                ReverseConditionTable table = null;
                var conditionTableFile = Path.Combine(BSPDirectory, ReverseFileConditionBuilder.ReverseConditionListFileName + ".gz");
                if (File.Exists(conditionTableFile))
                    table = XmlTools.LoadObject<ReverseConditionTable>(conditionTableFile);

                return new STM32SampleRelocator(sampleDir, table, _Ruleset);
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

                if (vs.SourceFiles.FirstOrDefault(f => f.Contains("libBle_Mesh_CM4_GCC")) != null)
                {
                    var dict = PropertyDictionary2.ReadPropertyDictionary(vs.Configuration.MCUConfiguration);
                    dict["com.sysprogs.bspoptions.arm.floatmode"] = "-mfloat-abi=soft";
                    vs.Configuration.MCUConfiguration = new PropertyDictionary2(dict);
                }

                if (vs.SourceFiles.FirstOrDefault(f => f.Contains("iar_cortexM4l_math.a")) != null)
                {
                    vs.SourceFiles = vs.SourceFiles.Where(f => !f.Contains("iar_cortexM4l_math.a")).ToArray();
                    if (vs.Configuration.Frameworks != null)
                        throw new NotSupportedException("Support concatenation");

                    vs.Configuration.Frameworks = new[] { "com.sysprogs.arm.stm32.dsp" };
                }

                for (int i = 0; i < vs.SourceFiles.Length; i++)
                {
                    const string suffix1 = @"IAR8.x\touchgfx_core_release.a";
                    if (vs.SourceFiles[i].EndsWith(suffix1, StringComparison.InvariantCultureIgnoreCase))
                    {
                        var replacement = vs.SourceFiles[i].Substring(0, vs.SourceFiles[i].Length - suffix1.Length) + @"gcc\libtouchgfx-float-abi-hard.a";
                        if (!File.Exists(replacement))
                            throw new Exception("Missing IAR->GCC substitute: " + replacement);

                        vs.SourceFiles[i] = replacement;
                    }
                }
            }

            const bool UseLegacySampleParser = false;

            protected override ParsedVendorSamples ParseVendorSamples(string SDKdir, IVendorSampleFilter filter)
            {
                var SDKs = XmlTools.LoadObject<STM32SDKCollection>(Path.Combine(BSPDirectory, "SDKVersions.xml"));

                bool isBlueNRG = _Ruleset == STM32Ruleset.BlueNRG_LP;

                if (isBlueNRG)
                    SDKs.SDKs = new STM32SDKCollection.SDK[] { new STM32SDKCollection.SDK { Family = "BlueNRG-LP", Version = "builtin" } };

                using (var parser = new SW4STM32ProjectParser(CacheDirectory, BSP.BSP.SupportedMCUs))
                {
                    List<VendorSample> allSamples = new List<VendorSample>();

                    foreach (var sdk in SDKs.SDKs)
                    {
                        List<string> addInc = new List<string>();
                        string topLevelDir;
                        if (isBlueNRG)
                            topLevelDir = SDKdir;
                        else
                            topLevelDir = Directory.GetDirectories(Path.Combine(SDKdir, sdk.FolderName), "STM32Cube_*")[0];

                        addInc.Add($@"{topLevelDir}\Drivers\CMSIS\Include");

                        if (!filter.ShouldParseAnySamplesInsideDirectory(topLevelDir))
                            continue;

                        int sampleCount = 0;
                        Console.WriteLine($"Discovering samples for {sdk.Family}...");

                        foreach (var boardDir in Directory.GetDirectories(Path.Combine(topLevelDir, "Projects")))
                        {
                            string boardName = Path.GetFileName(boardDir);
                            int samplesForThisBoard = 0;

                            if (UseLegacySampleParser)
                            {
                                foreach (var dir in Directory.GetDirectories(boardDir, "Mdk-arm", SearchOption.AllDirectories))
                                {
                                    string sampleName = Path.GetFileName(Path.GetDirectoryName(dir));

                                    if (!filter.ShouldParseAnySamplesInsideDirectory(dir))
                                        continue;   //We are only reparsing a subset of samples

                                    var aSamples = GetInfoProjectFromMDK(dir, topLevelDir, boardName, addInc);

                                    if (aSamples.Count != 0)
                                        Debug.Assert(aSamples[0].UserFriendlyName == sampleName);   //Otherwise quick reparsing won't work.

                                    foreach (var sample in aSamples)
                                        filter?.OnSampleParsed(sample);

                                    var scriptDir = Path.Combine(dir, "..", "SW4STM32");

                                    if (Directory.Exists(scriptDir))
                                    {
                                        string[] linkerScripts = Directory.GetFiles(scriptDir, "*.ld", SearchOption.AllDirectories);
                                        if (linkerScripts.Length != 0)
                                        {
                                            var distinctLinkerScripts = linkerScripts.Select(s => File.ReadAllText(s)).Distinct().Count();

                                            if (distinctLinkerScripts == 1)
                                            {
                                                foreach (var sample in aSamples)
                                                    sample.LinkerScript = Path.GetFullPath(linkerScripts[0]);
                                            }
                                            else
                                            {
                                                //Some sample projects define multiple linker scripts (e.g. STM32F072RBTx_FLASH.ld vs. STM32F072VBTx_FLASH.ld).
                                                //In this case we don't pick the sample-specific linker script and instead go with the regular BSP script for the selected MCU.
                                            }
                                        }
                                    }

                                    sampleCount += aSamples.Count;
                                    samplesForThisBoard += aSamples.Count;
                                    allSamples.AddRange(aSamples);
                                }
                            }

                            if (samplesForThisBoard == 0)
                            {
                                for (int pass = 0; pass < (isBlueNRG ? 1 : 2); pass++)
                                {
                                    string dirName;
                                    if (isBlueNRG)
                                        dirName = "WiSE-Studio";
                                    else if (pass == 0)
                                        dirName = "SW4STM32";
                                    else
                                        dirName = "STM32CubeIDE";

                                    foreach (var dir in Directory.GetDirectories(boardDir, dirName, SearchOption.AllDirectories))
                                    {
                                        if (!filter.ShouldParseAnySamplesInsideDirectory(Path.GetDirectoryName(dir)))
                                            continue;   //We are only reparsing a subset of samples

                                        SW4STM32ProjectParser.ProjectSubtype subtype;
                                        if (isBlueNRG)
                                        {
                                            subtype = SW4STM32ProjectParser.ProjectSubtype.WiSEStudio;
                                            if (boardName == "External_Micro")
                                                continue;
                                        }
                                        else if (pass == 0)
                                            subtype = SW4STM32ProjectParser.ProjectSubtype.SW4STM32;
                                        else
                                            subtype = SW4STM32ProjectParser.ProjectSubtype.STM32CubeIDE;

                                        var aSamples = parser.ParseProjectFolder(dir, topLevelDir, boardName, addInc, subtype);

                                        foreach (var sample in aSamples)
                                            filter?.OnSampleParsed(sample);

                                        sampleCount += aSamples.Count;
                                        samplesForThisBoard += aSamples.Count;
                                        allSamples.AddRange(aSamples);
                                    }
                                }
                            }

                        }

                        Console.WriteLine($"Found {sampleCount} samples for {sdk.Family}.");
                    }

                    return new ParsedVendorSamples { VendorSamples = allSamples.ToArray(), FailedSamples = parser.FailedSamples.ToArray() };
                }
            }

            class STM32CodeScopeModuleLocator : ICodeScopeModuleLocator
            {
                public CodeScopeSDKMatchingRule[] SDKMatchingRules => new[]
                {
                    new CodeScopeSDKMatchingRule(@"^([^_]+)_([0-9.]+)\\STM32Cube_FW_([^_]+)_V([0-9.]+)", "{1}", "{2}", ValidateFamilyMatch)
                };

                static void ValidateFamilyMatch(Match m)
                {
                    if (m.Groups[1].Value != m.Groups[3].Value)
                        throw new Exception("Mismatching family");
                    if (m.Groups[2].Value != m.Groups[4].Value)
                        throw new Exception("Mismatching version");
                }

                public CodeScopeModuleMatchingRule[] ModuleMatchingRules => new[]
                {
                    new CodeScopeModuleMatchingRule(@"Drivers\\STM32[^_\\]+_HAL_Driver", "HAL", CodeScopeModuleType.Core),
                    new CodeScopeModuleMatchingRule(@"Drivers\\CMSIS", @"CMSIS", CodeScopeModuleType.Core),
                    new CodeScopeModuleMatchingRule(@"Drivers\\BSP\\Components\\([^\\]+)", @"Drivers\Peripherals\{1}", CodeScopeModuleType.Driver),
                    new CodeScopeModuleMatchingRule(@"Drivers\\BSP\\Adafruit_Shield", @"Drivers\Peripherals\Adafruit_Shield", CodeScopeModuleType.Driver),
                    new CodeScopeModuleMatchingRule(@"Drivers\\BSP\\([^\\]+)", @"Drivers\Boards\{1}", CodeScopeModuleType.Driver),
                    new CodeScopeModuleMatchingRule(@"Middlewares\\(ST|Third_Party)\\([^\\]+)", @"Libraries\{2}", CodeScopeModuleType.Library),
                    new CodeScopeModuleMatchingRule(@"Utilities", @"Utilities", CodeScopeModuleType.Library),
                };

                public CodeScopeModuleMatchingRule[] SampleMatchingRules => new[]
                {
                    new CodeScopeModuleMatchingRule(@"Projects\\([^\\]+)", @"Examples\{1}\{2}", CodeScopeModuleType.Example),
                };
            }

            protected override ICodeScopeModuleLocator ModuleLocator { get; } = new STM32CodeScopeModuleLocator();
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
