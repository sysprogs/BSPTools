using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LinkerScriptGenerator;
using BSPEngine;
using System.IO;

namespace esp8266
{
    class ESP8266BSPBuilder : BSPBuilder
    {
        public ESP8266BSPBuilder(BSPDirectories dirs)
            : base(dirs)
        {
        }

        public override void GetMemoryBases(out uint flashBase, out uint ramBase)
        {
            throw new NotImplementedException();
        }

        public override MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
        {
            throw new NotImplementedException();
        }
    }


    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: esp8266.exe <ESP8266 SDK base directory>");

            var bspBuilder = new ESP8266BSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"));
            PathTools.CopyDirectoryRecursive(@"..\..\bsp-template", bspBuilder.Directories.OutputDir);

            var bsp = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(bspBuilder.BSPRoot, "bsp.xml"));

            var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));
            List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>(bsp.Frameworks);
            frameworks.AddRange(commonPseudofamily.GenerateFrameworkDefinitions());
            bsp.Frameworks = frameworks.ToArray();

            XmlTools.SaveObject(bsp, Path.Combine(bspBuilder.BSPRoot, "BSP.XML"));
        }
    }
}
