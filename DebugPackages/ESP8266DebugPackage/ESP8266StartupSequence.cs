using BSPEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ESP8266DebugPackage
{
    class ESP8266StartupSequence : ICustomStartupSequenceBuilder
    {
        public struct ProgrammableRegion
        {
            public int Offset;
            public int Size;
            public string FileName;
        }

        struct ParsedFLASHLoader
        {
            public readonly byte[] Data;
            public readonly uint LoadAddress;
            public readonly uint EntryPoint;
            public readonly uint ParameterArea;
            public readonly uint DataBuffer;
            public readonly int DataBufferSize;
            public readonly string FullPath;

            public readonly uint pCommand;
            public readonly uint pArg1;
            public readonly uint pArg2;
            public readonly uint pResult;

            public ParsedFLASHLoader(string fn)
            {
                Data = File.ReadAllBytes(fn);
                if (BitConverter.ToUInt32(Data, 0) != 0x48534C46)
                    throw new Exception("Signature mismatch");
                LoadAddress = BitConverter.ToUInt32(Data, 4);
                FullPath = fn;
                EntryPoint = BitConverter.ToUInt32(Data, 8);
                ParameterArea = BitConverter.ToUInt32(Data, 12);
                DataBuffer = BitConverter.ToUInt32(Data, 16);
                DataBufferSize = BitConverter.ToInt32(Data, 20);

                if (EntryPoint <= LoadAddress || EntryPoint >= LoadAddress + Data.Length)
                    throw new Exception("Invalid entry point for FLASH loader");

                pCommand = ParameterArea;
                pArg1 = ParameterArea + 4;
                pArg2 = ParameterArea + 8;
                pResult = ParameterArea + 12;
            }

            public CustomStartStep QueueInvocation(int cmd, string arg1, string arg2, string dataFile, int dataOffset, int dataSize, bool loadSelf = false, string error = null)
            {
                List<string> cmds = new List<string>();

                if (loadSelf)
                {
                    if (FullPath.Contains(" "))
                        throw new Exception("FLASH loader stub path cannot contain spaces: " + FullPath);
                    cmds.Add(string.Format("restore {0} binary 0x{1:x} 0 0x{2:x}", FullPath.Replace('\\', '/'), LoadAddress, Data.Length));
                }

                if (dataFile != null)
                {
                    if (dataFile.Contains(" "))
                        throw new Exception("Programmed file path cannot contain spaces: " + FullPath);

                    cmds.Add(string.Format("restore {0} binary 0x{1:x} 0x{2:x} 0x{3:x}", dataFile.Replace('\\', '/'), DataBuffer - dataOffset, dataOffset, dataOffset + dataSize));
                }

                cmds.Add(string.Format("flushregs", EntryPoint));
                cmds.Add(string.Format("set $epc2=0x{0:x}", EntryPoint));
                cmds.Add(string.Format("set $ps=0x20", EntryPoint));
                cmds.Add(string.Format("set $sp=$$DEBUG:INITIAL_STACK_POINTER$$"));
                cmds.Add(string.Format("set *((unsigned *)0x{0:x})={1}", pCommand, cmd));
                cmds.Add(string.Format("set *((unsigned *)0x{0:x})={1}", pArg1, arg1));
                cmds.Add(string.Format("set *((unsigned *)0x{0:x})={1}", pArg2, arg2));
                cmds.Add(string.Format("set *((unsigned *)0x{0:x})={1}", pResult, uint.MaxValue));
                cmds.Add(string.Format("set $intclear=-1"));
                cmds.Add(string.Format("set $intenable=0"));
                cmds.Add(string.Format("-exec-continue"));

                return new CustomStartStep(cmds.ToArray())
                {
                    ResultCheckExpression = string.Format("*((unsigned *)0x{0:x})", pResult),
                    ResultCheckCondition = new Condition.Equals { ExpectedValue = "0", Expression = "$$RESULT$$" },
                    ErrorMessage = error,
                    ProgressWeight = dataSize,
                    CanRetry = true,
                };
            }

            public void QueueRegionProgramming(List<CustomStartStep> cmds, ProgrammableRegion region)
            {
                cmds.Add(QueueInvocation(1, region.Offset.ToString(), region.Size.ToString(), null, 0, 0, false, string.Format("Failed to erase the FLASH region starting at 0x{0:x}", region.Offset)));

                for (int off = 0; off < region.Size; )
                {
                    int todo = Math.Min(region.Size - off, DataBufferSize);
                    int alignment = 4;
                    int alignedTodo = ((todo + alignment - 1) / alignment) * alignment;

                    cmds.Add(QueueInvocation(2, (region.Offset + off).ToString(), alignedTodo.ToString(), region.FileName, off, todo, false, string.Format("Failed to program the FLASH region starting at 0x{0:x}, offset 0x{1:x}, size 0x{2:x}", region.Offset, off, todo)));
                    off += todo;
                }
            }
        }
        public CustomStartupSequence BuildSequence(string targetPath, Dictionary<string, string> bspDict, Dictionary<string, string> debugMethodConfig, LiveMemoryLineHandler lineHandler)
        {
            List<CustomStartStep> cmds = new List<CustomStartStep>();
            cmds.Add(new CustomStartStep("maint packet R",
                "-exec-next-instruction",
                "set $com_sysprogs_esp8266_wdcfg=0",
                "set $vecbase=0x40000000",
                "$$com.sysprogs.esp8266.interrupt_disable_command$$",
                "set $ccompare=0",
                "set $intclear=-1",
                "set $intenable=0"));

            var result = new CustomStartupSequence { Steps = cmds };

            string val;
            if (bspDict.TryGetValue("com.sysprogs.esp8266.load_flash", out val) && val == "1")
            {
                if (debugMethodConfig.TryGetValue("com.sysprogs.esp8266.xt-ocd.program_flash", out val) && val != "0")
                {
                    string bspPath = bspDict["SYS:BSP_ROOT"];
                    List<ProgrammableRegion> regions = BuildFLASHImages(targetPath, bspDict, debugMethodConfig, lineHandler);

                    string loader = bspPath + @"\sysprogs\flashprog\ESP8266FlashProg.bin";
                    if (!File.Exists(loader))
                        throw new Exception("FLASH loader not found: " + loader);

                    var parsedLoader = new ParsedFLASHLoader(loader);

                    cmds.Add(new CustomStartStep("print *((int *)0x60000900)", "set *((int *)0x60000900)=0"));
                    cmds.Add(parsedLoader.QueueInvocation(0, "$$com.sysprogs.esp8266.xt-ocd.prog_sector_size$$", "$$com.sysprogs.esp8266.xt-ocd.erase_sector_size$$", null, 0, 0, true));
                    foreach (var region in regions)
                        parsedLoader.QueueRegionProgramming(cmds, region);

                    /*using (ELFFile elf = new ELFFile(targetPath))
                    {
                        foreach(var sec in elf.AllSections)
                        {
                            if (!sec.PresentInMemory || !sec.HasData || sec.Type != ELFFile.SectionType.SHT_PROGBITS)
                                continue;

                            bool isInRAM = false;
                            if (sec.VirtualAddress >= 0x3FFE8000 && sec.VirtualAddress < (0x3FFE8000 + 81920))
                                isInRAM = true;
                            else if (sec.VirtualAddress >= 0x40100000 && sec.VirtualAddress <= (0x40100000 + 32768))
                                isInRAM = true;

                            if (isInRAM)
                            {
                                cmds.Add(new FLASHProgrammingStep("restore {0} binary 0x{1:x} 0x{2:x} 0x{3:x}", targetPath.Replace('\\', '/'),
                                    sec.VirtualAddress - sec.OffsetInFile, sec.OffsetInFile, sec.OffsetInFile + sec.Size) { CheckResult = true, ErrorMessage = "Failed to program the " + sec.SectionName + " section" });
                            }
                        }
                    }*/
                }

                if (debugMethodConfig.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_start_mode", out val) && val == "soft_reset")
                {
                    cmds.Add(new CustomStartStep("set $ps=0x20",
                    "set $epc2=0x40000080",
                    "set $sp=$$DEBUG:INITIAL_STACK_POINTER$$",
                    "set $vecbase=0x40000000",
                    "$$com.sysprogs.esp8266.interrupt_disable_command$$",
                    "set $intclear=-1",
                    "set $intenable=0"));
                    result.InitialHardBreakpointExpression = "*$$DEBUG:ENTRY_POINT$$";
                }
                else
                    cmds.Add(new CustomStartStep("maint packet R"));
            }
            else
            {
                cmds.Add(new CustomStartStep("load"));
                cmds.Add(new CustomStartStep("set $ps=0x20"));
                cmds.Add(new CustomStartStep("set $epc2=$$DEBUG:ENTRY_POINT$$"));
                cmds.Add(new CustomStartStep("set $sp=$$DEBUG:INITIAL_STACK_POINTER$$"));
                cmds.Add(new CustomStartStep("set $vecbase=0x40000000"));
                cmds.Add(new CustomStartStep("$$com.sysprogs.esp8266.interrupt_disable_command$$"));
                cmds.Add(new CustomStartStep("set $ccompare=0"));
                cmds.Add(new CustomStartStep("set $intclear=-1"));
                cmds.Add(new CustomStartStep("set $intenable=0"));
            }

            return result;
        }

        public static List<ProgrammableRegion> BuildFLASHImages(string targetPath, Dictionary<string, string> bspDict, Dictionary<string, string> debugMethodConfig, LiveMemoryLineHandler lineHandler)
        {
            string bspPath = bspDict["SYS:BSP_ROOT"];
            string toolchainPath = bspDict["SYS:TOOLCHAIN_ROOT"];

            string pythonPath = Path.GetDirectoryName(bspPath) + @"\python27\python.exe";
            if (!File.Exists(pythonPath))
                throw new Exception("Python not found: " + pythonPath);

            string esptoolPath = bspPath + @"\esptool.py";
            if (!File.Exists(esptoolPath))
                throw new Exception("Esptool not found: " + esptoolPath);

            Regex rgBinFile = new Regex("^" + Path.GetFileName(targetPath) + "-0x([0-9a-fA-F]+)\\.bin$", RegexOptions.IgnoreCase);
            foreach (var fn in Directory.GetFiles(Path.GetDirectoryName(targetPath)))
                if (rgBinFile.IsMatch(Path.GetFileName(fn)))
                    File.Delete(fn);

            string val;
            string args = string.Format("\"{0}\" elf2image \"{1}\"", esptoolPath, targetPath);
            if (debugMethodConfig.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_freq", out val) && !string.IsNullOrEmpty(val))
                args += " --flash_freq " + val;
            if (debugMethodConfig.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_mode", out val) && !string.IsNullOrEmpty(val))
                args += " --flash_mode " + val;
            if (debugMethodConfig.TryGetValue("com.sysprogs.esp8266.xt-ocd.flash_size", out val) && !string.IsNullOrEmpty(val))
                args += " --flash_size " + val;

            if (lineHandler != null)
                lineHandler(pythonPath + " " + args, false);

            var startInfo = new ProcessStartInfo { FileName = pythonPath, Arguments = args, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            startInfo.EnvironmentVariables["PATH"] += ";" + toolchainPath + @"\bin";

            var proc = Process.Start(startInfo);
            if (lineHandler != null)
            {
                proc.OutputDataReceived += (p, e) => lineHandler(e.Data, true);
                proc.ErrorDataReceived += (p, e) => lineHandler(e.Data, true);
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit(30000))
            {
                proc.Kill();
                throw new Exception("ESPTool appears to be hanging");
            }

            if (proc.ExitCode != 0)
                throw new Exception("ESPTool returned an error " + proc.ExitCode);

            List<ProgrammableRegion> regions = new List<ProgrammableRegion>();

            foreach (var fn in Directory.GetFiles(Path.GetDirectoryName(targetPath)))
            {
                var m = rgBinFile.Match(Path.GetFileName(fn));
                if (m.Success)
                    regions.Add(new ProgrammableRegion { FileName = fn, Offset = int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber), Size = File.ReadAllBytes(fn).Length });
            }

            if (regions.Count == 0)
                throw new Exception("ESPTool did not produce any binary files");
            return regions;
        }

        public string ID
        {
            get { return "com.sysprogs.esp8266.load_sequence"; }
        }


        public string FirstStepName
        {
            get { return "Preparing image"; }
        }

        public string SecondStepName
        {
            get { return "Loading image"; }
        }

        public string Title
        {
            get { return "Loading ESP8266 firmware"; }
        }
    }
}
