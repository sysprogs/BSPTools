using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LinkerScriptGenerator;
using BSPEngine;
using System.IO;
using System.Text.RegularExpressions;

namespace esp32
{
    class Program
    {
        static void Main(string[] args)
        {
            //This generator updates the BSP definition based on the latest ESP-IDF
            if (args.Length < 1)
                throw new Exception("Usage: esp32.exe <toolchain directory>");

            var toolchainDir = args[0];
            var bspXml = Path.Combine(toolchainDir, "esp32-bsp", "BSP.xml");
            var bsp = XmlTools.LoadObject<BoardSupportPackage>(bspXml);

            var idfDirs = Directory.GetDirectories(Path.Combine(toolchainDir, "esp-idf")).Where(d => StringComparer.InvariantCultureIgnoreCase.Compare(Path.GetFileName(d), "SysprogsComponents") != 0).ToArray();
            if (idfDirs.Length != 1)
                throw new Exception("Found multiple ESP-IDF directories");

            var idfDir = idfDirs[0];
            var socDirs = Directory.GetDirectories(Path.Combine(idfDir, "components", "soc"), "esp32*");
            foreach(var socDir in socDirs)
            {
                var socName = Path.GetFileName(socDir);

                var registers = PeripheralRegisterParser.ParsePeripheralRegisters(socDir);
                XmlTools.SaveObject(new MCUDefinition { MCUName = socName.ToUpper(), RegisterSets = registers }, Path.Combine(toolchainDir, "esp32-bsp", "peripherals", socName + ".xml"));
            }

            foreach(var mcu in bsp.SupportedMCUs)
            {
                mcu.MCUDefinitionFile = $"peripherals/{mcu.ID.ToLower()}.xml";
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(bspXml), mcu.MCUDefinitionFile)))
                    throw new Exception("Missing " + mcu.MCUDefinitionFile);
            }

            XmlTools.SaveObject(bsp, bspXml);
        }

    }
}
