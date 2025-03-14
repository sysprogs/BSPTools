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
    class STM32H7RSFamilyBuilder : MCUFamilyBuilder
    {
        public STM32H7RSFamilyBuilder(BSPBuilder bspBuilder, FamilyDefinition definition)
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
            string sourcePath = "$$STM32:H7RS_DIR$$/Drivers/CMSIS/Device/ST/STM32H7RSxx/Source/Templates/gcc/linker";
            BSP.ExpandVariables(ref sourcePath);

            var targetPath = Path.Combine(BSP.Directories.OutputDir, $"{FamilyFilePrefix}LinkerScripts");

            DeviceListProviders.AssignLinkerScripts(targetPath, FamilyFilePrefix, MCUs, sourcePath);
            return null;
        }

    }
}
