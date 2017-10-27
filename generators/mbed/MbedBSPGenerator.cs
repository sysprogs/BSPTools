using BSPEngine;
using BSPGenerationTools;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace mbed
{
    class MbedBSPGenerator
    {
        public readonly string outputDir = Path.GetFullPath(@"..\..\Output");
        public readonly string dataDir = Path.GetFullPath(@"..\..\data");
        string mbedRoot;
        string toolchainDir = "e:\\sysgcc\\arm-eabi";
        public List<KeyValuePair<Regex, string>> nameRules;
        private Dictionary<string, string> mcuDefs;

        public readonly string Version;

        public MbedBSPGenerator(string version)
        {
            Directory.CreateDirectory(outputDir);
            mbedRoot = Path.GetFullPath(Path.Combine(outputDir, "mbed"));
            Version = version;

            nameRules = new List<KeyValuePair<Regex, string>>();
            foreach (var line in File.ReadAllLines(Path.Combine(dataDir, "DeviceNameRules.txt")))
            {
                int idx = line.IndexOf('=');
                nameRules.Add(new KeyValuePair<Regex, string>(new Regex(line.Substring(0, idx).Trim()), line.Substring(idx + 1).Trim()));
            }

            LoadMCUDefinitionsFromOtherBSPs();
        }

        private void LoadMCUDefinitionsFromOtherBSPs()
        {
            mcuDefs = new Dictionary<string, string>();
            var linkedBSPs = Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\VisualGDB\EmbeddedBSPs\arm-eabi", "*.bsplink").Select(f => File.ReadAllText(f));
            foreach (var tmpDir in Directory.GetDirectories(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\VisualGDB\EmbeddedBSPs\arm-eabi").Concat(linkedBSPs))
            {
                string dir = tmpDir;
                if (Path.GetFileName(dir).ToLower() == "mbed")
                    continue;

                if (File.Exists(Path.Combine(dir, "MultipleBSPVersions.txt")))
                {
                    var subdirs = Directory.GetDirectories(dir).Select(d => Path.GetFileName(d)).ToList();
                    subdirs.Sort();
                    dir = Path.Combine(dir, subdirs.Last());
                }

                var anotherBSP = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(dir, "bsp.xml"));
                foreach (var mcu in anotherBSP.SupportedMCUs)
                {
                    if (mcu.MCUDefinitionFile != null)
                        mcuDefs[mcu.ID] = Path.Combine(dir, mcu.MCUDefinitionFile);
                }
            }
        }

        public void UpdateGitAndRescanTargets()
        {
            string gitExe = (Registry.CurrentUser.OpenSubKey(@"Software\Sysprogs\BSPGenerators")?.GetValue("git") as string) ?? "git.exe";
            string pythonExe = (Registry.CurrentUser.OpenSubKey(@"Software\Sysprogs\BSPGenerators")?.GetValue("python") as string) ?? "python.exe";

            Process proc;
            if (Directory.Exists(mbedRoot))
            {
                proc = Process.Start(new ProcessStartInfo(gitExe, "reset --hard") { WorkingDirectory = mbedRoot, UseShellExecute = false });
            }
            else
            {
                proc = Process.Start(new ProcessStartInfo(gitExe, $"clone https://github.com/ARMmbed/mbed-os.git mbed") { WorkingDirectory = outputDir, UseShellExecute = false });
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                    throw new Exception("Git exited with code " + proc.ExitCode);

                proc = Process.Start(new ProcessStartInfo(gitExe, $"checkout mbed-os-{Version}") { WorkingDirectory = outputDir + "\\mbed", UseShellExecute = false });

            }
            proc.WaitForExit();

            if (proc.ExitCode != 0)
                throw new Exception("Git exited with code " + proc.ExitCode);

            foreach (var lds in Directory.GetFiles(mbedRoot, "*.ld", SearchOption.AllDirectories))
            {
                if (File.ReadAllText(lds).Contains("\n#if"))
                {
                    ProcessStartInfo preprocessInfo = new ProcessStartInfo($@"{toolchainDir}\bin\arm-eabi-cpp.exe", $"-P -C {lds} -o {lds}.preprocessed");
                    preprocessInfo.UseShellExecute = false;
                    preprocessInfo.EnvironmentVariables["PATH"] += $@";{toolchainDir}\bin";
                    proc = Process.Start(preprocessInfo);
                    proc.WaitForExit();

                    File.Copy(lds + ".preprocessed", lds, true);
                    File.Delete(lds + ".preprocessed");
                }
            }

            var patchedFile = Path.Combine(mbedRoot, @"tools\config\__init__.py");
            var lines = File.ReadAllLines(patchedFile).ToList();
            var idx2 = Enumerable.Range(0, lines.Count).First(i => lines[i].Contains("self.value = int(value) if isinstance(value, bool) else value"));
            if (!lines[idx2 + 1].Contains("is_bool"))
            {
                lines.Insert(idx2 + 1, "        self.is_bool = isinstance(value, bool)  #Patch by Sysprogs");
                File.WriteAllLines(patchedFile, lines);
            }

            //7. Enable exporting LPC targets
            patchedFile = Path.Combine(mbedRoot, @"tools\export\exporters.py");
            lines = File.ReadAllLines(patchedFile).ToList();
            string str = "target.post_binary_hook['function'] in whitelist:";
            idx2 = Enumerable.Range(0, lines.Count).FirstOrDefault(i => lines[i].Contains(str));
            if (idx2 > 0)
            {
                int subIdx = lines[idx2].IndexOf(str);
                lines[idx2] = lines[idx2].Substring(0, subIdx) + "True:";
                File.WriteAllLines(patchedFile, lines);
            }

            string sampleDir = Path.Combine(mbedRoot, "samples");
            if (Directory.Exists(sampleDir))
                Directory.Delete(sampleDir, true);
            PathTools.CopyDirectoryRecursive(Path.Combine(dataDir, "samples"), sampleDir);

            ProcessStartInfo bspGenInfo = new ProcessStartInfo(pythonExe, Path.Combine(dataDir, "BuildConfigExtractor.py"));
            bspGenInfo.UseShellExecute = false;
            bspGenInfo.EnvironmentVariables["PYTHONPATH"] = mbedRoot;
            bspGenInfo.EnvironmentVariables["PATH"] += $@";{toolchainDir}\bin";
            proc = Process.Start(bspGenInfo);
            proc.WaitForExit();

            if (proc.ExitCode != 0)
                throw new Exception("BSP generator exited with code " + proc.ExitCode);
        }

        public void PatchBuggyFiles()
        {
            File.Copy(Path.Combine(dataDir, "stubs.cpp"), Path.Combine(mbedRoot, "stubs.cpp"), true);

            //1. Missing bsp.h for Nordic
            File.WriteAllText(Path.Combine(mbedRoot, @"targets\TARGET_NORDIC\bsp.h"), "#pragma once\n#define BSP_INDICATE_FATAL_ERROR 0\nvoid __attribute__((weak)) bsp_indication_set() { }\n");

            //2. Reference to missing O_BINARY
            string patchedFile = Path.Combine(mbedRoot, @"platform\mbed_retarget.cpp");
            var lines = File.ReadAllLines(patchedFile).ToList();
            int idx2 = Enumerable.Range(0, lines.Count).First(l => lines[l].Contains("posix &= ~O_BINARY"));
            if (!lines[idx2 - 1].Contains("O_BINARY"))
            {
                lines[idx2 - 1] += " && defined(O_BINARY)";
                File.WriteAllLines(patchedFile, lines);
            }

            //3. Missing sys/types.h
            patchedFile = Path.Combine(mbedRoot, @"targets\TARGET_NORDIC\TARGET_MCU_NRF51822\TARGET_DELTA_DFCM_NNN40\rtc_api.c");
            lines = File.ReadAllLines(patchedFile).ToList();
            if (lines.FirstOrDefault(l => l.Contains("sys/types.h")) == null)
            {
                int idx = Enumerable.Range(0, lines.Count)
                    .SkipWhile(i => !lines[i].StartsWith("#include"))
                    .SkipWhile(i => lines[i].StartsWith("#include"))
                    .First();

                lines.Insert(idx, "#include <sys/types.h>");
                File.WriteAllLines(patchedFile, lines);
            }

            //4. Missing "omit frame pointer" attribute
            foreach (var fn in Directory.GetFiles(mbedRoot, "bootloader_util.c", SearchOption.AllDirectories))
            {
                patchedFile = fn;
                lines = File.ReadAllLines(patchedFile).ToList();
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i] == "#elif defined ( __GNUC__ )" && lines[i + 1] == "static inline void bootloader_util_reset(uint32_t start_addr)")
                    {
                        lines[i + 1] = "static inline __attribute__((optimize(\"-fomit-frame-pointer\"))) void bootloader_util_reset(uint32_t start_addr)";
                        File.WriteAllLines(patchedFile, lines);
                        break;
                    }
                }
            }


            //6. omit stack pointer
            foreach (var fn in Directory.GetFiles(mbedRoot, "rt_CMSIS.c", SearchOption.AllDirectories))
            {
                lines = File.ReadAllLines(fn).ToList();
                if (lines.Count(l => l.Contains("-fomit-frame-pointer")) == 0)
                {
                    idx2 = Enumerable.Range(0, lines.Count).First(i => lines[i].Contains("#define os_thread_cb OS_TCB"));
                    lines.Insert(idx2 + 1, "#pragma GCC optimize (\"-fomit-frame-pointer\")");
                    File.WriteAllLines(patchedFile, lines);
                }
            }
        }

        public string[] ConvertPaths(IEnumerable<string> rawPaths)
        {
            return rawPaths.Select(fn =>
            {
                if (fn.Length == mbedRoot.Length && fn.ToLower() == mbedRoot.ToLower())
                    return "$$SYS:BSP_ROOT$$";
                if (fn.StartsWith(mbedRoot, StringComparison.InvariantCulture))
                    return "$$SYS:BSP_ROOT$$/" + fn.Substring(mbedRoot.Length + 1).Replace('\\', '/');
                throw new Exception("Source path is not inside the mbed BSP: " + fn);
            }).ToArray();
        }

        public struct ConvertedHexFile
        {
            public uint LoadAddress;
            public int Size;
            public string RelativePath;
            public string SectionName;
        }

        Dictionary<string, ConvertedHexFile> _ConvertedHexFiles = new Dictionary<string, ConvertedHexFile>();
        Dictionary<string, string> _PatchedLinkerScripts = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        Dictionary<string, string> _PatchedLinkerScriptsReverse = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

        public void ConvertSoftdevicesAndPatchTarget(MCU mcu, string[] hexFiles)
        {
            string baseDir = Path.Combine(mbedRoot, "SysprogsGenerated");
            Directory.CreateDirectory(baseDir);
            File.WriteAllText(Path.Combine(baseDir, ".mbedignore"), "*");

            foreach (var hexFile in hexFiles)
            {
                if (_ConvertedHexFiles.ContainsKey(hexFile))
                    continue;
                var parsedFile = IntelHexParser.Parse(hexFile);
                string sectionName = Path.GetFileNameWithoutExtension(hexFile).Replace('.', '_').Replace('-', '_');
                string generatedCFile = $@"{baseDir}\{Path.GetFileNameWithoutExtension(hexFile)}.c";

                using (var fs = File.CreateText(generatedCFile))
                {
                    fs.WriteLine($"//Converted from " + Path.GetFileName(hexFile));
                    fs.WriteLine($"//Must be loaded at 0x{parsedFile.LoadAddress:x8}");
                    fs.WriteLine($"const char __attribute__((used, section(\".{sectionName}\"))) {sectionName}[] = " + "{");
                    fs.Write("\t");
                    int cnt = 0;
                    foreach (var b in parsedFile.Data)
                    {
                        if (cnt != 0 && (cnt % 16) == 0)
                        {
                            fs.WriteLine();
                            fs.Write("\t");
                        }
                        cnt++;
                        fs.Write($"0x{b:x2}, ");
                    }
                    fs.WriteLine();
                    fs.WriteLine("};");
                    fs.WriteLine();
                }

                if (!generatedCFile.StartsWith(mbedRoot, StringComparison.InvariantCultureIgnoreCase))
                    throw new Exception("HEX file outside mbed root");
                _ConvertedHexFiles[hexFile] = new ConvertedHexFile { LoadAddress = parsedFile.LoadAddress, RelativePath = generatedCFile.Substring(mbedRoot.Length + 1), SectionName = sectionName, Size = parsedFile.Data.Length };
            }

            var hexFileList = hexFiles.Select(hf => _ConvertedHexFiles[hf]).ToList();
            if (hexFiles.Length == 0)
                return;

            hexFileList.Sort((a, b) => a.LoadAddress.CompareTo(b.LoadAddress));
            var linkerScript = mcu.CompilationFlags.LinkerScript;
            string patchedLinkerScript;

            if (!_PatchedLinkerScripts.TryGetValue(linkerScript, out patchedLinkerScript))
            {
                patchedLinkerScript = Path.Combine(baseDir, Path.GetFileName(mcu.CompilationFlags.LinkerScript));
                if (_PatchedLinkerScriptsReverse.TryGetValue(patchedLinkerScript, out string tmp) && tmp != linkerScript)
                {
                    for (int i = 2; ; i++)
                    {
                        patchedLinkerScript = Path.Combine(baseDir, Path.GetFileNameWithoutExtension(mcu.CompilationFlags.LinkerScript) + "_x" + i + Path.GetExtension(mcu.CompilationFlags.LinkerScript));
                        if (!_PatchedLinkerScriptsReverse.ContainsKey(patchedLinkerScript))
                            break;
                    }

                }
                _PatchedLinkerScripts[linkerScript] = patchedLinkerScript;
                _PatchedLinkerScriptsReverse[patchedLinkerScript] = linkerScript;

                List<string> linkerScriptLines = File.ReadAllLines(linkerScript.Replace("$$SYS:BSP_ROOT$$", mbedRoot)).ToList();

                int firstMemorySectionLine = Enumerable.Range(0, linkerScript.Length)
                    .SkipWhile(i => linkerScriptLines[i].Trim() != "MEMORY")
                    .SkipWhile(i => !linkerScriptLines[i].Contains("{"))
                    .First();

                int offset = 1;
                foreach (var hex in hexFileList)
                    linkerScriptLines.Insert(firstMemorySectionLine + offset++, $"  {hex.SectionName} (rx) : ORIGIN = 0x{hex.LoadAddress:x8}, LENGTH = 0x{hex.Size:x}");


                int sectionsLine = Enumerable.Range(0, linkerScript.Length)
                              .SkipWhile(i => linkerScriptLines[i].Trim() != "SECTIONS")
                              .SkipWhile(i => !linkerScriptLines[i].Contains("{"))
                              .First();

                offset = 1;

                foreach (var hex in hexFileList)
                {
                    char br1 = '{', br2 = '}';
                    string contents = $".{hex.SectionName} :\n{br1}\n\tKEEP(*(.{hex.SectionName}))\n{br2} > {hex.SectionName}\n";
                    foreach (var line in contents.Split('\n'))
                        linkerScriptLines.Insert(sectionsLine + offset++, "\t" + line);
                }

                File.WriteAllLines(patchedLinkerScript.Replace("$$SYS:BSP_ROOT$$", mbedRoot), linkerScriptLines);
            }

            mcu.CompilationFlags.LinkerScript = ConvertPaths(new[] { patchedLinkerScript })[0];
            mcu.AdditionalSourceFiles = mcu.AdditionalSourceFiles.Concat(hexFileList.Select(h => "$$SYS:BSP_ROOT$$/" + h.RelativePath.Replace('\\', '/'))).ToArray();
        }

        public void DetectAndApplyMemorySizes(MCU mcu, string linkerScript)
        {
            Console.Write(".");
            string tmpFile = Path.GetTempPath() + "LinkerScriptQuery.c";
            File.WriteAllText(tmpFile, "");
            string mapFile = Path.ChangeExtension(tmpFile, ".map");
            var proc = Process.Start(new ProcessStartInfo(Path.Combine(toolchainDir, @"bin\arm-eabi-gcc.exe"), $"-T {linkerScript} {tmpFile} -Wl,-Map,{mapFile} -Wl,--defsym,__Vectors=0 -Wl,--defsym,Stack_Size=0")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            proc.WaitForExit();
            var rgMemory = new Regex("^([^ \t]+)[ \t]+0x([0-9a-fA-F]+)[ \t]+0x([0-9a-fA-F]+)");
            var memories = File.ReadAllLines(mapFile)
                            .SkipWhile(l => !l.Contains("Memory Configuration"))
                            .TakeWhile(l => !l.Contains("Linker script and memory map"))
                            .Select(l => rgMemory.Match(l))
                            .Where(m => m.Success)
                            .Select(m => new MCUMemory { Name = m.Groups[1].Value, Address = uint.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.AllowHexSpecifier), Size = uint.Parse(m.Groups[3].Value, System.Globalization.NumberStyles.AllowHexSpecifier) })
                            .Where(m => m.Name != "*default*")
                            .ToArray();

            mcu.MemoryMap = new AdvancedMemoryMap { Memories = memories };
            var flash = memories.FirstOrDefault(m => m.Name.ToUpper() == "FLASH" || m.Name == "m_text" || m.Name == "ROM" || m.Name == "rom" || m.Name == "MFlash256");
            var ram = memories.First(m => m.Name.ToUpper() == "RAM" || m.Name == "m_data" || m.Name == "RAM_INTERN" || m.Name == "SRAM1" || m.Name == "RAM0" || m.Name.StartsWith("Ram0_"));

            if (flash == null)
            {
                if (mcu.ID != "LPC4330_M4" && mcu.ID != "LPC4337"&& mcu.ID != "LPC4330_M0")
                    throw new Exception("Could not locate FLASH memory");
            }
            else
            {
                mcu.FLASHSize = (int)flash.Size;
                mcu.FLASHBase = (uint)flash.Address;
            }

            mcu.RAMSize = (int)ram.Size;
            mcu.RAMBase = (uint)ram.Address;
        }

        internal void CopyAndAttachRegisterDefinitions(MCU mcu)
        {
            foreach (var rule in nameRules)
            {
                var m = rule.Key.Match(mcu.ID);
                if (m.Success)
                {
                    string devRegex = rule.Value;
                    for (int i = 1; i < m.Groups.Count; i++)
                        devRegex = devRegex.Replace(@"\" + i, m.Groups[i].Value);

                    Regex devRegexObj = new Regex(devRegex);
                    string definition = null;
                    foreach (var dev in mcuDefs)
                    {
                        if (devRegexObj.IsMatch(dev.Key))
                            definition = dev.Value;
                    }

                    if (definition == null)
                        Console.WriteLine("Warning: cannot find device register definition for " + devRegex);
                    else
                    {
                        mcu.MCUDefinitionFile = "DeviceDefinitions/" + Path.GetFileName(definition);
                        string targetPath = Path.Combine(mbedRoot, mcu.MCUDefinitionFile + ".gz");
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                        File.Copy(definition + ".gz", targetPath, true);
                    }
                    break;
                }
            }
        }

        public void ProduceBSPArchive(BoardSupportPackage bsp)
        {
            //bsp.PackageVersion = string.Format("{0:d4}{1:d2}{2:d2}", DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day);
            //bsp.PackageVersion += "-beta";
            XmlTools.SaveObject(bsp, Path.Combine(mbedRoot, "BSP.XML"));

            string archiveName = string.Format("{0}-{1}.vgdbxbsp", bsp.PackageID.Split('.').Last(), bsp.PackageVersion);
            Console.WriteLine("Creating BSP archive...");

            TarPacker.PackDirectoryToTGZ(mbedRoot, Path.Combine(Path.GetDirectoryName(mbedRoot), archiveName), fn =>
            {
                string relPath = fn.Substring(mbedRoot.Length + 1);
                if (relPath.StartsWith(".git"))
                    return false;
                if (relPath.ToLower() == "ParsedTargets.xml".ToLower())
                    return false;
                return true;
            }, subdir => !subdir.StartsWith(".git", StringComparison.CurrentCultureIgnoreCase));

            var lst = new BSPGenerationTools.BSPSummary
            {
                BSPName = bsp.PackageDescription,
                BSPID = bsp.PackageID,
                BSPVersion = bsp.PackageVersion,
                MinimumEngineVersion = bsp.MinimumEngineVersion,
                FileName = archiveName,
            };

            foreach (var mcu in bsp.SupportedMCUs)
                lst.MCUs.Add(new BSPGenerationTools.BSPSummary.MCU { Name = mcu.ID, FLASHSize = mcu.FLASHSize, RAMSize = mcu.RAMSize });

            XmlTools.SaveObject(lst, Path.Combine(Path.GetDirectoryName(mbedRoot), Path.ChangeExtension(archiveName, ".xml")));
        }

        public string[] DetectSampleDirs()
        {
            return Directory.GetDirectories(Path.Combine(mbedRoot, "samples"))
                .Where(d => File.Exists(Path.Combine(d, "sample.xml")))
                .Select(d => "samples/" + Path.GetFileName(d))
                .ToArray();
        }
    }
}
