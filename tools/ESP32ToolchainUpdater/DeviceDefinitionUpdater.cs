using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ESP32ToolchainUpdater
{
    public class DeviceDefinitionUpdater
    {
        public static string UpdateBSP(string toolchainDir, string sampleProjectsBase)
        {
            var bspXML = Path.Combine(toolchainDir, @"esp32-bsp\BSP.xml");
            var bsp = XmlTools.LoadObject<BoardSupportPackage>(bspXML);

            var idfDir = Directory.GetDirectories(Path.Combine(toolchainDir, "esp-idf")).Single();
            var devices = Directory.GetDirectories(Path.Combine(idfDir, @"components\esp_hw_support\port")).Select(f => Path.GetFileName(f)).Where(d => d.StartsWith("esp32")).ToArray();

            var newDevices = new List<MCU>();
            foreach (var dev in devices)
            {
                var bspDev = bsp.SupportedMCUs.FirstOrDefault(x => StringComparer.OrdinalIgnoreCase.Compare(x.ID, dev) == 0);
                if (bspDev == null)
                {
                    newDevices.Add(new MCU
                    {
                        ID = dev.ToUpper(),
                        FamilyID = "ESP32",
                        MainFunctionName = "user_init",
                        AdditionalSystemVars = new[]
                        {
                            new SysVarEntry
                            {
                                Key = "com.sysprogs.esp32.load_flash",
                                Value = "1",
                            }
                        }
                    });
                }
            }

            bsp.SupportedMCUs = bsp.SupportedMCUs.Concat(newDevices).ToArray();
            var memoryNames = MemoryMapUpdater.GetXtensaMemoryMappings();

            foreach (var mcu in bsp.SupportedMCUs)
            {
                var projectDir = Path.Combine(sampleProjectsBase, mcu.ID.ToLower());
                var mapFile = Path.Combine(projectDir, @"get-started\blink\build\blink.map");
                if (!File.Exists(mapFile))
                    throw new Exception("Missing " + mapFile);

                var memories = MemoryMapUpdater.LocateMemories(mapFile).OrderBy(m => m.Start).ToList();
                for (int i = 1; i < memories.Count; i++)
                {
                    if (memories[i].Start < memories[i - 1].End)
                    {
                        memories.RemoveAt(i--);
                        continue;
                    }
                }

                mcu.MemoryMap = new AdvancedMemoryMap
                {
                    Memories = memories.Select(m => TranslateMemory(m, memoryNames)).ToArray(),
                };

                var memList = MemoryMapUpdater.CanonicalMemoryOrder.ToList();

                var memoriesWithOrder = Enumerable.Range(0, mcu.MemoryMap.Memories.Count()).Select(i => new MemoryWithOrder { Memory = mcu.MemoryMap.Memories[i], Order = 1000 + i }).ToArray();
                foreach(var m in memoriesWithOrder)
                {
                    int index = memList.IndexOf(m.Memory.Name);
                    if (index >= 0)
                        m.Order = index;
                }
                mcu.MemoryMap.Memories = memoriesWithOrder.OrderBy(m => m.Order).Select(m => m.Memory).ToArray();

                var fn = Path.GetFullPath($@"..\..\esp32-svd\svd\{mcu.ID}.svd");
                if (File.Exists(fn))
                {
                    var registerSet = SVDParser.ParseSVDFile(fn, mcu.ID);

                    mcu.MCUDefinitionFile ??= "peripherals/" + mcu.ID.ToLower() + ".xml";

                    var mcuDef = Path.Combine(toolchainDir, "esp32-bsp", mcu.MCUDefinitionFile);
                    XmlTools.SaveObject(registerSet, mcuDef);
                }
            }

            XmlTools.SaveObject(bsp, bspXML);
            return bspXML;
        }


        class MemoryWithOrder
        {
            public MCUMemory Memory;
            public int Order;
        }
        private static MCUMemory TranslateMemory(MemoryMapUpdater.MemoryExtents m, Dictionary<string, string> memoryNames)
        {
            var result = new MCUMemory
            {
                Address = m.Start,
                Size = m.Length,
            };

            if (!memoryNames.TryGetValue(m.Name, out result.Name))
            {
                if (m.Name.EndsWith("_seg"))
                    result.Name = m.Name.Substring(0, m.Name.Length - 4).ToUpper();
                else
                    result.Name = m.Name.ToUpper();
            }

            return result;
        }
    }
}
