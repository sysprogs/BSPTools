/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using BSPGenerationTools;
using System;
using System.Linq;

namespace kinetis_bsp_generator {

    class Program {

        static void Main(string[] args) {

            if (args.Length < 1) {
                throw new Exception("Usage: kinetis_bsp_generator.exe <Kinetis SDK directory>");
            }

            var bspBuilder = new KinetisBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"), !args.Contains("/noperiph"));
            bspBuilder.GeneratePackage();
        } 
    }
}
