using BSPGenerationTools.Parsing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace stm32_bsp_generator
{
    static class PeripheralRegisterGenerator2
    {
        public static void TestEntry()
        {
            foreach(var fn in Directory.GetFiles(@"E:\temp\stm32registers", "*.h"))
            {
                var parser = new HeaderFileParser(fn);

                var parsedFile = parser.ParseHeaderFile();
            }
        }
    }
}
