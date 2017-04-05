using BSPEngine;
using BSPGenerationTools;
using Microsoft.Win32;
using StandaloneBSPValidator;
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
    public class TestInfo
    {
        public TestInfo(string Filename, int Passed, int Failed)
        {
            this.Filename = Filename;
            this.Passed = Passed;
            this.Failed = Failed;
        }
        public string Filename { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
    }

    class IntelHexParser
    {
        struct IntelHexRecord
        {
            public ushort Address;
            public byte RecordType;
            public byte[] Data;

            public static ushort SwapBytes(ushort x)
            {
                return (ushort)((ushort)((x & 0xff) << 8) | ((x >> 8) & 0xff));
            }

            internal static IntelHexRecord Parse(string line)
            {
                line = line.Trim();
                if (!line.StartsWith(":"))
                    throw new InvalidOperationException("Invalid Intel HEX line: " + line);

                byte[] parsedBytes = new byte[(line.Length - 1) / 2];
                for (int i = 0; i < parsedBytes.Length; i++)
                    parsedBytes[i] = byte.Parse(line.Substring(1 + i * 2, 2), System.Globalization.NumberStyles.HexNumber);

                byte byteCount = parsedBytes[0];

                //Warning: we do not verify the record size or the checksum!
                return new IntelHexRecord
                {
                    Address = SwapBytes(BitConverter.ToUInt16(parsedBytes, 1)),
                    RecordType = parsedBytes[3],
                    Data = parsedBytes.Skip(4).Take(byteCount).ToArray()
                };
            }
        }

        public static uint GetLoadAddress(string ihexFile)
        {
            using (var fs = File.OpenText(ihexFile))
            {
                var line0 = IntelHexRecord.Parse(fs.ReadLine());
                var line1 = IntelHexRecord.Parse(fs.ReadLine());
                uint segmentBase;
                if (line0.RecordType == 2)
                    segmentBase = IntelHexRecord.SwapBytes(BitConverter.ToUInt16(line0.Data, 0)) * 16U;
                else if (line0.RecordType == 4)
                    segmentBase = (uint)IntelHexRecord.SwapBytes(BitConverter.ToUInt16(line0.Data, 0)) << 16;
                else
                    throw new Exception($"{ihexFile} does not start with a record of type 2");

                return segmentBase + line1.Address;
            }
        }

        public struct ParsedIntelHexFile
        {
            public uint LoadAddress;
            public byte[] Data;
        }

        public static ParsedIntelHexFile Parse(string hexFile)
        {
            uint segmentBase = 0;
            List<byte> data = new List<byte>();
            uint? start = null;

            foreach (var line in File.ReadAllLines(hexFile))
            {
                var parsedLine = IntelHexRecord.Parse(line);

                if (parsedLine.RecordType == 2)
                    segmentBase = IntelHexRecord.SwapBytes(BitConverter.ToUInt16(parsedLine.Data, 0)) * 16U;
                else if (parsedLine.RecordType == 4)
                    segmentBase = (uint)IntelHexRecord.SwapBytes(BitConverter.ToUInt16(parsedLine.Data, 0)) << 16;
                else if (parsedLine.RecordType == 0)
                {
                    uint addr = parsedLine.Address + segmentBase;
                    if (!start.HasValue)
                        start = addr;

                    if (addr != (start.Value + data.Count))
                    {
                        int padding = (int)(addr - (start.Value + data.Count));
                        if (padding < 0 || padding > 4096)
                            throw new Exception("Unexpected gap in " + hexFile);
                        for (int i = 0; i < padding; i++)
                            data.Add(0);
                    }
                    data.AddRange(parsedLine.Data);

                }
                else if (parsedLine.RecordType == 1)
                    break;
                else
                    throw new Exception($"Unexpected record type {parsedLine.RecordType} in {hexFile}");
            }

            return new ParsedIntelHexFile { LoadAddress = start.Value, Data = data.ToArray() };
        }
    }

    class Program
    {
        struct ConvertedHexFile
        {
            public uint LoadAddress;
            public int Size;
            public string RelativePath;
            public string SectionName;
        }

        static void Main(string[] args)
        {
            string outputDir = Path.GetFullPath(@"..\..\Output");
            string dataDir = Path.GetFullPath(@"..\..\data");
            Directory.CreateDirectory(outputDir);
            string mbedRoot = Path.Combine(outputDir, "mbed");

            string toolchainDir = "e:\\sysgcc\\arm-eabi";

            bool regenerate = true;
            if (regenerate)
            {
                string gitExe = (Registry.CurrentUser.OpenSubKey(@"Software\Sysprogs\BSPGenerators")?.GetValue("git") as string) ?? "git.exe";
                string pythonExe = (Registry.CurrentUser.OpenSubKey(@"Software\Sysprogs\BSPGenerators")?.GetValue("python") as string) ?? "python.exe";

                Process proc;
                if (Directory.Exists(mbedRoot))
                {
                    // Prevent pull fail due to modified files
                    proc = Process.Start(new ProcessStartInfo(gitExe, "reset --hard") { WorkingDirectory = mbedRoot, UseShellExecute = false });
                    proc.WaitForExit();
                    if (proc.ExitCode != 0)
                        throw new Exception("Git reset command exited with code " + proc.ExitCode);
                    proc = Process.Start(new ProcessStartInfo(gitExe, "pull origin 5.4.2") { WorkingDirectory = mbedRoot, UseShellExecute = false });
                }
                else
                    proc = Process.Start(new ProcessStartInfo(gitExe, "clone https://github.com/ARMmbed/mbed-os.git -b 5.4.2 mbed") { WorkingDirectory = outputDir, UseShellExecute = false });
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw new Exception("Git exited with code " + proc.ExitCode);

                foreach(var lds in Directory.GetFiles(mbedRoot, "*.ld", SearchOption.AllDirectories))
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


                string sampleDir = Path.Combine(mbedRoot, "samples");
                if (Directory.Exists(sampleDir))
                    Directory.Delete(sampleDir, true);
                PathTools.CopyDirectoryRecursive(Path.Combine(dataDir, "samples"), sampleDir);

                ProcessStartInfo bspGenInfo = new ProcessStartInfo(pythonExe, Path.Combine(dataDir, "visualgdb_bsp.py") + " --alltargets");
                bspGenInfo.UseShellExecute = false;
                bspGenInfo.EnvironmentVariables["PYTHONPATH"] = mbedRoot;
                bspGenInfo.EnvironmentVariables["PATH"] += $@";{toolchainDir}\bin";
                proc = Process.Start(bspGenInfo);
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                    throw new Exception("BSP generator exited with code " + proc.ExitCode);
            }

            List<KeyValuePair<Regex, string>> nameRules = new List<KeyValuePair<Regex, string>>();
            foreach (var line in File.ReadAllLines(Path.Combine(dataDir, "DeviceNameRules.txt")))
            {
                int idx = line.IndexOf('=');
                nameRules.Add(new KeyValuePair<Regex, string>(new Regex(line.Substring(0, idx).Trim()), line.Substring(idx + 1).Trim()));
            }

            Dictionary<string, List<ConvertedHexFile>> bootloaderFilesForTargets = new Dictionary<string, List<ConvertedHexFile>>();
            List<ConvertedHexFile> lst = null;
            foreach (var line in File.ReadAllLines(Path.Combine(mbedRoot, "hexfiles.txt")))
            {
                if (!line.StartsWith("\t"))
                    bootloaderFilesForTargets[line] = lst = new List<ConvertedHexFile>();
                else
                {
                    var hexFile = line.Trim();
                    var parsedFile = IntelHexParser.Parse(hexFile);
                    string sectionName = Path.GetFileNameWithoutExtension(hexFile).Replace('.', '_');
                    string generatedCFile = Path.ChangeExtension(hexFile, ".c");
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
                    lst.Add(new ConvertedHexFile { LoadAddress = parsedFile.LoadAddress, RelativePath = generatedCFile.Substring(mbedRoot.Length + 1), SectionName = sectionName, Size = parsedFile.Data.Length });
                }
            }

            File.Copy(Path.Combine(dataDir, "stubs.cpp"), Path.Combine(mbedRoot, "stubs.cpp"), true);

            Dictionary<string, string> mcuDefs = new Dictionary<string, string>();
            var linkedBSPs = Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\VisualGDB\EmbeddedBSPs\arm-eabi", "*.bsplink").Select(f => File.ReadAllText(f));
            foreach (var dir in Directory.GetDirectories(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\VisualGDB\EmbeddedBSPs\arm-eabi").Concat(linkedBSPs))
            {
                if (Path.GetFileName(dir).ToLower() == "mbed")
                    continue;
                var anotherBSP = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(dir, "bsp.xml"));
                foreach (var mcu in anotherBSP.SupportedMCUs)
                {
                    if (mcu.MCUDefinitionFile != null)
                        mcuDefs[mcu.ID] = Path.Combine(dir, mcu.MCUDefinitionFile);
                }
            }

            string bspFile = Path.Combine(mbedRoot, "BSP.xml");
            var bsp = XmlTools.LoadObject<BoardSupportPackage>(bspFile);
            var defDir = Directory.CreateDirectory(Path.Combine(mbedRoot, "DeviceDefinitions"));
            foreach (var fam in bsp.MCUFamilies)
                fam.AdditionalHeaderFiles = fam.AdditionalHeaderFiles.Where(f => !f.EndsWith("/.")).ToArray();

            PatchBuggyFiles(mbedRoot);

            foreach (var mcu in bsp.SupportedMCUs)
            {
                mcu.CompilationFlags.PreprocessorMacros = mcu.CompilationFlags.PreprocessorMacros.Where(m => !m.StartsWith("MBED_BUILD_TIMESTAMP=")).ToArray();

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
                            File.Copy(definition + ".gz", Path.Combine(mbedRoot, mcu.MCUDefinitionFile + ".gz"), true);
                        }
                        break;
                    }
                }

                List<ConvertedHexFile> hexFileList;
                if (bootloaderFilesForTargets.TryGetValue(mcu.ID, out hexFileList) && !mcu.CompilationFlags.LinkerScript.Contains("_patched.ld"))
                {
                    hexFileList.Sort((a, b) => a.LoadAddress.CompareTo(b.LoadAddress));
                    var linkerScript = mcu.CompilationFlags.LinkerScript;
                    var patchedLinkerScript = Path.ChangeExtension(mcu.CompilationFlags.LinkerScript, "").TrimEnd('.') + "_patched.ld";
                    List<string> linkerScriptLines = File.ReadAllLines(linkerScript.Replace("$$SYS:BSP_ROOT$$", mbedRoot)).ToList();

                    int firstMemorySectionLine = Enumerable.Range(0, linkerScript.Length)
                        .SkipWhile(i => linkerScriptLines[i].Trim() != "MEMORY")
                        .SkipWhile(i => !linkerScriptLines[i].Contains("{"))
                        .First();

                    int offset = 1;
                    foreach (var hex in hexFileList)
                        linkerScriptLines.Insert(firstMemorySectionLine + offset++, $"  {hex.SectionName} (rx) : ORIGIN = 0x{hex.LoadAddress:x8}, LENGTH = 0x{hex.Size}");

                    mcu.CompilationFlags.LinkerScript = patchedLinkerScript;

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

                    mcu.AdditionalSourceFiles = mcu.AdditionalSourceFiles.Concat(hexFileList.Select(h => "$$SYS:BSP_ROOT$$/" + h.RelativePath.Replace('\\', '/'))).ToArray();
                }
            }

            ProduceBSPArchive(mbedRoot, bsp);

            var testfFiles = new TestInfo[] { new TestInfo("test_usbcd.xml", 0, 0), new TestInfo("test_ledblink_rtos.xml", 0, 0), new TestInfo("test_ledblink.xml", 0, 0), };
            bool performTests = true;
            if (performTests)
            {
                foreach (var test in testfFiles)
                {
                    Console.WriteLine($"Testing {test.Filename}...");
                    var job = XmlTools.LoadObject<TestJob>(Path.Combine(dataDir, test.Filename));
                    if (job.ToolchainPath.StartsWith("["))
                    {
                        job.ToolchainPath = (string)Registry.CurrentUser.OpenSubKey(@"Software\Sysprogs\GNUToolchains").GetValue(job.ToolchainPath.Trim('[', ']'));
                        if (job.ToolchainPath == null)
                            throw new Exception("Cannot locate toolchain path from registry");
                    }
                    var toolchain = LoadedToolchain.Load(Environment.ExpandEnvironmentVariables(job.ToolchainPath), new ToolchainRelocationManager());
                    var lbsp = LoadedBSP.Load(new BSPManager.BSPSummary(Environment.ExpandEnvironmentVariables(Path.Combine(outputDir, "mbed"))), toolchain);
                    var r = StandaloneBSPValidator.Program.TestBSP(job, lbsp, Path.Combine(outputDir, "TestResults"));
                    test.Passed = r.Passed;
                    test.Failed = r.Failed;
                }

                foreach (var test in testfFiles)
                {
                    Console.WriteLine("Results for the test: " + test.Filename);
                    Console.WriteLine("Passed: " + test.Passed.ToString());
                    Console.WriteLine("Failed: " + test.Failed.ToString());
                    Console.WriteLine();
                }
            }
        }

        private static void PatchBuggyFiles(string mbedRoot)
        {
            //1. Missing bsp.h for Nordic
            File.WriteAllText(Path.Combine(mbedRoot, @"targets\TARGET_NORDIC\bsp.h"), "#pragma once\n#define BSP_INDICATE_FATAL_ERROR 0\n");

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
            if (lines.FirstOrDefault(l=>l.Contains("sys/types.h")) == null)
            {
                int idx = Enumerable.Range(0, lines.Count)
                    .SkipWhile(i => !lines[i].StartsWith("#include"))
                    .SkipWhile(i => lines[i].StartsWith("#include"))
                    .First();

                lines.Insert(idx, "#include <sys/types.h>");
                File.WriteAllLines(patchedFile, lines);
            }
        }

        static void ProduceBSPArchive(string BSPRoot, BoardSupportPackage bsp)
        {
            bsp.PackageVersion = string.Format("{0:d4}{1:d2}{2:d2}", DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day);
            bsp.PackageVersion += "-beta";
            XmlTools.SaveObject(bsp, Path.Combine(BSPRoot, "BSP.XML"));

            string archiveName = string.Format("{0}-{1}.vgdbxbsp", bsp.PackageID.Split('.').Last(), bsp.PackageVersion);
            Console.WriteLine("Creating BSP archive...");

            TarPacker.PackDirectoryToTGZ(BSPRoot, Path.Combine(Path.GetDirectoryName(BSPRoot), archiveName), fn =>
            {
                string relPath = fn.Substring(BSPRoot.Length + 1);
                if (relPath.StartsWith(".git"))
                    return false;
                return true;
            }, subdir => !subdir.StartsWith(".git", StringComparison.CurrentCultureIgnoreCase));

            BSPSummary lst = new BSPSummary
            {
                BSPName = bsp.PackageDescription,
                BSPID = bsp.PackageID,
                BSPVersion = bsp.PackageVersion,
                MinimumEngineVersion = bsp.MinimumEngineVersion,
                FileName = archiveName,
            };

            foreach (var mcu in bsp.SupportedMCUs)
                lst.MCUs.Add(new BSPSummary.MCU { Name = mcu.ID, FLASHSize = mcu.FLASHSize, RAMSize = mcu.RAMSize });

            XmlTools.SaveObject(lst, Path.Combine(Path.GetDirectoryName(BSPRoot), Path.ChangeExtension(archiveName, ".xml")));
        }
    }
}
