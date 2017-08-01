using BSPEngine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ESP8266DebugPackage
{
    static class ESP32StartupSequence
    {
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
