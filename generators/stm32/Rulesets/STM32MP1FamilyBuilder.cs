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
    class STM32MP1FamilyBuilder : MCUFamilyBuilder
    {
        public STM32MP1FamilyBuilder(BSPBuilder bspBuilder, FamilyDefinition definition)
            : base(bspBuilder, definition)
        {
        }

        public override MCUFamily GenerateFamilyObject(CoreSpecificFlags flagsToGenerate, bool allowExcludingStartupFiles = false)
        {
            //STM32MP1 devices use a predefined linker script, so we cannot override the primary memory.
            return base.GenerateFamilyObject(flagsToGenerate & ~CoreSpecificFlags.PrimaryMemory, allowExcludingStartupFiles);
        }

        public override MemoryLayoutCollection GenerateLinkerScripts(bool generalizeWherePossible)
        {
            foreach (var mcu in MCUs)
            {
                var stMCU = (DeviceListProviders.CubeProvider.STM32MCUBuilder)mcu;
                if (stMCU.PredefinedLinkerScripts.Length != 1)
                    throw new Exception($"Unexpected predefined linker scripts for {mcu.Name}");

                string relPath = $"{FamilyFilePrefix}LinkerScripts/{Path.GetFileName(stMCU.PredefinedLinkerScripts[0])}";
                mcu.LinkerScriptPath = "$$SYS:BSP_ROOT$$/" + relPath;
                string sourcePath = "$$STM32:MP1_DIR$$/Drivers/CMSIS/" + stMCU.PredefinedLinkerScripts[0];
                var targetPath = Path.Combine(BSP.Directories.OutputDir, relPath);
                BSP.ExpandVariables(ref sourcePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                File.Copy(sourcePath, targetPath, true);
            }

            return null;
        }

    }
}
