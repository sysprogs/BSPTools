using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace stm32_bsp_generator
{
    static class ManualPeripheralRegisterRules
    {
        internal static void ApplyKnownNameTransformations(ref string[] nameForMatching)
        {
            if (nameForMatching[0] == "OSPEEDER")
                nameForMatching[0] = "OSPEEDR";
        }
    }
}
