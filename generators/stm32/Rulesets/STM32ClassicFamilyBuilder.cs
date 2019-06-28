using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace stm32_bsp_generator.Rulesets
{
    class STM32ClassicFamilyBuilder : MCUFamilyBuilder
    {
        public STM32ClassicFamilyBuilder(BSPBuilder bspBuilder, FamilyDefinition definition) : base(bspBuilder, definition)
        {
        }

        protected override void OnMissingSampleFile(MissingSampleFileArgs args)
        {
            return;
            base.OnMissingSampleFile(args);
        }
    }
}
