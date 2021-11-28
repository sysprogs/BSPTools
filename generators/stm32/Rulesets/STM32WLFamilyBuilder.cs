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
    class STM32WLFamilyBuilder : MCUFamilyBuilder
    {
        public STM32WLFamilyBuilder(BSPBuilder bspBuilder, FamilyDefinition definition)
            : base(bspBuilder, definition)
        {
        }

    }
}
