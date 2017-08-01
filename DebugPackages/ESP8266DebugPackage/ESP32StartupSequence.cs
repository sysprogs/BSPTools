using BSPEngine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ESP8266DebugPackage
{
    class ESP32StartupSequence : ICustomStartupSequenceBuilder
    {
        public string ID => "com.sysprogs.esp8266.load_sequence";
        public string FirstStepName => "Preparing image";
        public string SecondStepName => "Loading image";
        public string Title => "Loading ESP32 firmware";

        struct ParsedFLASHLoader
        {
            public readonly byte[] Data;
            public readonly uint LoadAddress;
            public readonly uint EntryPoint;
            public readonly uint InfiniteLoop;
            public readonly uint Reset;
            public readonly uint DataBuffer;
            public readonly int DataBufferSize;
            public readonly uint EndOfStack;
            public readonly string FullPath;


            public ParsedFLASHLoader(string fn)
            {
                Data = File.ReadAllBytes(fn);
                if (BitConverter.ToUInt32(Data, 0) != 0x32332B46)
                    throw new Exception("Signature mismatch");
                LoadAddress = BitConverter.ToUInt32(Data, 4);
                FullPath = fn;
                EntryPoint = BitConverter.ToUInt32(Data, 8);
                InfiniteLoop = BitConverter.ToUInt32(Data, 12);
                Reset = BitConverter.ToUInt32(Data, 16);
                DataBuffer = BitConverter.ToUInt32(Data, 20);
                DataBufferSize = BitConverter.ToInt32(Data, 24);
                EndOfStack = BitConverter.ToUInt32(Data, 28) + BitConverter.ToUInt32(Data, 32);

                if (EntryPoint <= LoadAddress || EntryPoint >= LoadAddress + Data.Length)
                    throw new Exception("Invalid entry point for FLASH loader");
                if (InfiniteLoop <= LoadAddress || InfiniteLoop >= LoadAddress + Data.Length)
                    throw new Exception("Invalid infinite loop stub for FLASH loader");
                if (Reset <= LoadAddress || Reset >= LoadAddress + Data.Length)
                    throw new Exception("Invalid reset function for FLASH loader");
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

                cmds.Add($"mon esp108 run_alg 0x{EntryPoint:x8} 0x{InfiniteLoop:x8} $$com.sysprogs.esp32.openocd.alg_timeout$$ a1=0x{EndOfStack:x8} a10={cmd} a11={arg1} a12={arg2} a0 pc");

                return new CustomStartStep(cmds.ToArray())
                {
                    ResultCheckExpression = "@a0 = (.*)",
                    ResultCheckCondition = new Condition.Equals { ExpectedValue = "0x0", Expression = "$$RESULT$$" },
                    ErrorMessage = error,
                    ProgressWeight = dataSize,
                    CanRetry = true,
                };
            }

            public CustomStartStep QueueResetStep()
            {
                return new CustomStartStep($"mon esp108 run_alg 0x{EntryPoint:x8} 0x{InfiniteLoop:x8} 1000 a1=0x{EndOfStack:x8} a10=3 a0 pc");
            }

            public void QueueRegionProgramming(List<CustomStartStep> cmds, ProgrammableRegion region, int eraseBlockSize, bool eraseStage)
            {
                if (eraseStage)
                {
                    int eraseSize = ((region.Size + eraseBlockSize - 1) / eraseBlockSize) * eraseBlockSize;
                    cmds.Add(QueueInvocation(1, region.Offset.ToString(), eraseSize.ToString(), null, 0, 0, false, string.Format("Failed to erase the FLASH region starting at 0x{0:x}", region.Offset)));
                }
                else
                {
                    for (int off = 0; off < region.Size;)
                    {
                        int todo = Math.Min(region.Size - off, DataBufferSize);
                        int alignment = 4;
                        int alignedTodo = ((todo + alignment - 1) / alignment) * alignment;

                        cmds.Add(QueueInvocation(2, (region.Offset + off).ToString(), alignedTodo.ToString(), region.FileName, off, todo, false, string.Format("Failed to program the FLASH region starting at 0x{0:x}, offset 0x{1:x}, size 0x{2:x}", region.Offset, off, todo)));
                        off += todo;
                    }
                }
            }
        }

        public CustomStartupSequence BuildSequence(string targetPath, Dictionary<string, string> bspDict, Dictionary<string, string> debugMethodConfig, LiveMemoryLineHandler lineHandler)
        {
            List<CustomStartStep> cmds = new List<CustomStartStep>();
            cmds.Add(new CustomStartStep("mon reset halt"));

            string bspPath = bspDict["SYS:BSP_ROOT"];

            string val;
            if (bspDict.TryGetValue("com.sysprogs.esp32.load_flash", out val) && val == "1")
            {
                //Not a FLASHless project, FLASH loading required
                if (debugMethodConfig.TryGetValue("com.sysprogs.esp8266.xt-ocd.program_flash", out val) && val != "0")
                {
                    string loader = bspPath + @"\sysprogs\flashprog\ESP32FlashProg.bin";
                    if (!File.Exists(loader))
                        throw new Exception("FLASH loader not found: " + loader);

                    var parsedLoader = new ParsedFLASHLoader(loader);

                    //List<ProgrammableRegion> regions = new List<ProgrammableRegion>();
                    //regions.Add(new ProgrammableRegion { FileName = @"E:\temp\esp\build\bootloader\bootloader.bin", Offset = 0x1000, Size = 4224 });
                    //regions.Add(new ProgrammableRegion { FileName = @"E:\temp\esp\build\blink.bin", Offset = 0x10000, Size = 245328 });
                    //regions.Add(new ProgrammableRegion { FileName = @"E:\temp\esp\build\partitions_singleapp.bin", Offset = 0x4000, Size = 96 });

                    /*var regions = BuildFLASHImages(targetPath, bspDict, debugMethodConfig, lineHandler);

                    int eraseBlockSize = int.Parse(debugMethodConfig["com.sysprogs.esp8266.xt-ocd.erase_sector_size"]);
                    cmds.Add(parsedLoader.QueueInvocation(0, "$$com.sysprogs.esp8266.xt-ocd.prog_sector_size$$", "0", null, 0, 0, true));
                    for (int pass = 0; pass < 2; pass++)
                        foreach (var region in regions)
                            parsedLoader.QueueRegionProgramming(cmds, region, eraseBlockSize, pass == 0);

                    cmds.Add(parsedLoader.QueueResetStep());*/
                }
                else
                    cmds.Add(new CustomStartStep("mon esp108 chip_reset"));
            }
            else
            {
                cmds.Add(new CustomStartStep("load"));
            }

            return new CustomStartupSequence { Steps = cmds };
        }

        public static List<ProgrammableRegion> BuildFLASHImages(string targetPath, Dictionary<string, string> bspDict, ESP8266BinaryImage.ParsedHeader flashSettings)
        {
            string bspPath = bspDict["SYS:BSP_ROOT"];
            string toolchainPath = bspDict["SYS:TOOLCHAIN_ROOT"];

            string partitionTable, bootloader, txtAppOffset;
            bspDict.TryGetValue("com.sysprogs.esp32.partition_table_file", out partitionTable);
            bspDict.TryGetValue("com.sysprogs.esp32.bootloader_file", out bootloader);
            bspDict.TryGetValue("com.sysprogs.esp32.app_offset", out txtAppOffset);

            uint appOffset;
            if (txtAppOffset == null)
                appOffset = 0;
            else if (txtAppOffset.StartsWith("0x"))
                uint.TryParse(txtAppOffset.Substring(2), NumberStyles.HexNumber, null, out appOffset);
            else
                uint.TryParse(txtAppOffset, out appOffset);

            if (appOffset == 0)
                throw new Exception("Application FLASH offset not defined. Please check your settings.");

            partitionTable = VariableHelper.ExpandVariables(partitionTable, bspDict);
            bootloader = VariableHelper.ExpandVariables(bootloader, bspDict);

            if (!string.IsNullOrEmpty(partitionTable) && !Path.IsPathRooted(partitionTable))
                partitionTable = Path.Combine(bspDict["SYS:PROJECT_DIR"], partitionTable);
            if (!string.IsNullOrEmpty(bootloader) && !Path.IsPathRooted(bootloader))
                bootloader = Path.Combine(bspDict["SYS:PROJECT_DIR"], bootloader);

            if (string.IsNullOrEmpty(partitionTable) || !File.Exists(partitionTable))
                throw new Exception("Unspecified or missing partition table file: " + partitionTable);
            if (string.IsNullOrEmpty(bootloader) || !File.Exists(bootloader))
                throw new Exception("Unspecified or missing bootloader file: " + bootloader);

            List<ProgrammableRegion> regions = new List<ProgrammableRegion>();

            using (var elfFile = new ELFFile(targetPath))
            {
                string pathBase = Path.Combine(Path.GetDirectoryName(targetPath), Path.GetFileName(targetPath));

                var img = ESP8266BinaryImage.MakeESP32ImageFromELFFile(elfFile, flashSettings);

                //Bootloader/partition table offsets are hardcoded in ESP-IDF
                regions.Add(new ProgrammableRegion { FileName = bootloader, Offset = 0x1000, Size = GetFileSize(bootloader) });
                regions.Add(new ProgrammableRegion { FileName = partitionTable, Offset = 0x8000, Size = GetFileSize(partitionTable) });

                string fn = pathBase + "-esp32.bin";
                using (var fs = new FileStream(fn, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    img.Save(fs);
                    regions.Add(new ProgrammableRegion { FileName = fn, Offset = (int)appOffset, Size = (int)fs.Length });
                }
            }
            return regions;
        }

        private static int GetFileSize(string fn)
        {
            using (var fs = new FileStream(fn, FileMode.Open, FileAccess.Read))
                return (int)fs.Length;
        }
    }
}
