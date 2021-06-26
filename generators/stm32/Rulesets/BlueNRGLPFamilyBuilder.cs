using BSPEngine;
using BSPGenerationTools;
using LinkerScriptGenerator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace stm32_bsp_generator.Rulesets
{
    class BlueNRGFamilyBuilder : MCUFamilyBuilder
    {
        public BlueNRGFamilyBuilder(BSPBuilder bspBuilder, FamilyDefinition definition)
            : base(bspBuilder, definition)
        {
        }

        public override MCUFamily GenerateFamilyObject(CoreSpecificFlags flagsToGenerate, bool allowExcludingStartupFiles = false)
        {
            //BlueNRG-LP devices use a predefined linker script, so we cannot override the primary memory.
            return base.GenerateFamilyObject(flagsToGenerate & ~CoreSpecificFlags.PrimaryMemory, allowExcludingStartupFiles);
        }

        public override MemoryLayoutCollection GenerateLinkerScripts(bool generalizeWherePossible)
        {
            foreach (var mcu in MCUs)
            {
                var sourcePath = mcu.LinkerScriptPath;
                
                string relPath = $"{FamilyFilePrefix}LinkerScripts/{Path.GetFileName(sourcePath)}";
                mcu.LinkerScriptPath = "$$SYS:BSP_ROOT$$/" + relPath;
                var targetPath = Path.Combine(BSP.Directories.OutputDir, relPath);
                BSP.ExpandVariables(ref sourcePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                File.Copy(sourcePath, targetPath, true);
            }

            return null;
        }

        public override void AttachStartupFiles(IEnumerable<StartupFileGenerator.InterruptVectorTable> files, string startupFileFolder = "StartupFiles", string pFileNameTemplate = "StartupFileTemplate.c")
        {
            MCUs.Single().StartupFile = "$$SYS:BSP_ROOT$$/CMSIS/Device/ST/BlueNRG_LP/Source/system_BlueNRG_LP.c";
        }

        public class BlueNRGMCUBuilder : MCUBuilder
        {
            public BlueNRGMCUBuilder()
            {
                Name = "BlueNRG-LP";
                FlashSize = 256 * 1024;
                RAMSize = 64 * 1024;
                Core = CortexCore.M0;
                LinkerScriptPath = "$$BSPGEN:INPUT_DIR$$/Projects/BLE_Examples/BLE_Beacon/WiSE-Studio/STEVAL-IDB011V1/BlueNRG_LP.ld";
                AttachedMemoryLayout = new MemoryLayout
                {
                    Memories = new List<Memory>
                    {
                        new Memory
                        {
                            Type = MemoryType.FLASH,
                            Start = 0x10040000,
                            Size = 0x40000,
                            Access = MemoryAccess.Undefined,
                            IsPrimary = true,
                            Name = "FLASH",
                        },
                        new Memory
                        {
                            Type = MemoryType.RAM,
                            Start = 0x20000000,
                            Size = 0x10000,
                            Access = MemoryAccess.Undefined,
                            IsPrimary = true,
                            Name = "RAM",
                        },
                    }
                };
            }
        }
    }
}
