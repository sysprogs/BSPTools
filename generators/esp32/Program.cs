using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LinkerScriptGenerator;
using BSPEngine;
using System.IO;

namespace esp32
{
    class ESP32BSPBuilder : BSPBuilder
    {
        public ESP32BSPBuilder(BSPDirectories dirs)
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
                throw new Exception("Usage: esp32.exe <esp-idf directory>");

            var bspBuilder = new ESP32BSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"));
            PathTools.CopyDirectoryRecursive(@"..\..\bsp-template", bspBuilder.Directories.OutputDir);

            string registerSetFile = Path.Combine(bspBuilder.Directories.OutputDir, "registers.xml");
            var registers = PeripheralRegisterParser.ParsePeripheralRegisters(Path.Combine(bspBuilder.Directories.InputDir, "esp-idf"));
            XmlTools.SaveObject(new MCUDefinition { MCUName = "ESP32", RegisterSets = registers }, registerSetFile);

            var bsp = XmlTools.LoadObject<BoardSupportPackage>(Path.Combine(bspBuilder.BSPRoot, "bsp.xml"));

            var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));
            List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>(bsp.Frameworks);
            frameworks.AddRange(commonPseudofamily.GenerateFrameworkDefinitions());
            bsp.Frameworks = frameworks.ToArray();

            List<string> projectFiles = new List<string>();

            if (commonPseudofamily.Definition.CoreFramework != null)
                foreach (var job in commonPseudofamily.Definition.CoreFramework.CopyJobs)
                    job.CopyAndBuildFlags(bspBuilder, projectFiles, null);

            var mainFamily = bsp.MCUFamilies.First();

            if (mainFamily.AdditionalSourceFiles != null || mainFamily.AdditionalHeaderFiles != null || bsp.FileConditions != null)
                throw new Exception("TODO: merge lists");

            mainFamily.AdditionalSourceFiles = projectFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).ToArray();
            mainFamily.AdditionalHeaderFiles = projectFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).ToArray();
            bsp.FileConditions = bspBuilder.MatchedFileConditions.ToArray();

            foreach(var fn in Directory.GetFiles(Path.Combine(bspBuilder.Directories.OutputDir, @"esp-idf\components\nghttp"), "*.?", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(fn).ToLower();
                if (ext != ".c" && ext != ".h")
                    continue;

                var lines = File.ReadAllLines(fn);
                bool changed = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("<config.h>"))
                    {
                        lines[i] = lines[i].Replace("<config.h>", "<nghttp-config.h>");
                        changed = true;
                    }
                }
                if (changed)
                    File.WriteAllLines(fn, lines);
            }

            foreach (var mcu in bsp.SupportedMCUs)
                mcu.MCUDefinitionFile = Path.GetFileName(registerSetFile);

            File.WriteAllText(Path.Combine(bspBuilder.Directories.OutputDir, @"esp-idf\components\nghttp\port\include\nghttp-config.h"), "#include \"config.h\"\n");
            XmlTools.SaveObject(bsp, Path.Combine(bspBuilder.BSPRoot, "BSP.XML"));
        }
    }
}
