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

        struct ParsedLinkerScript
        {
            public string DeviceQualifier, Suffix, FileName, FileNameWithoutExtension;

            public int SortOrder
            {
                get
                {
                    if (Suffix == "flash")
                        return 0;
                    else if (Suffix == "sram")
                        return 1;
                    else
                        return 100;
                }
            }

            public ParsedLinkerScript(string fn)
            {
                int idx = fn.IndexOf('_');
                if (idx < 0 || !fn.EndsWith(".ld"))
                    throw new Exception("Unexpected linker script name: " + fn);

                FileName = fn;
                FileNameWithoutExtension = fn.Substring(0, fn.Length - 3);

                DeviceQualifier = fn.Substring(0, idx);
                Suffix = fn.Substring(idx + 1, fn.Length - idx - 4);
            }

            public override string ToString() => FileName;

            public bool IsCompatibleWith(MCUBuilder mcu)
            {
                switch(DeviceQualifier)
                {
                    case "stm32h7r3xx":
                    case "stm32h7r7xx":
                    case "stm32h7s3xx":
                    case "stm32h7s7xx":
                        return mcu.Name.StartsWith(DeviceQualifier.Substring(0, 9), StringComparison.InvariantCultureIgnoreCase);
                    case "stm32h7rsxx":
                        return true;
                    default:
                        throw new NotImplementedException("Unsupported linker script qualifier: " + DeviceQualifier);
                }
            }

            public PropertyEntry.Enumerated.Suggestion ToSuggestion()
            {
                var s = new PropertyEntry.Enumerated.Suggestion
                {
                    InternalValue = FileNameWithoutExtension,
                    UserFriendlyName = Suffix,
                };

                if (Suffix == "flash" || Suffix == "sram")
                    s.UserFriendlyName = s.UserFriendlyName.ToUpper();

                return s;
            }
        }

        public override MemoryLayoutCollection GenerateLinkerScripts(bool generalizeWherePossible)
        {
            string sourcePath = "$$STM32:H7RS_DIR$$/Drivers/CMSIS/Device/ST/STM32H7RSxx/Source/Templates/gcc/linker";
            BSP.ExpandVariables(ref sourcePath);
            var targetPath = Path.Combine(BSP.Directories.OutputDir, $"{FamilyFilePrefix}LinkerScripts");
            Directory.CreateDirectory(targetPath);
            var linkerScripts = Directory.GetFiles(sourcePath).Select(fn => new ParsedLinkerScript(Path.GetFileName(fn))).ToArray();
            foreach(var n in linkerScripts)
                File.Copy(Path.Combine(sourcePath, n.FileName), Path.Combine(targetPath, n.FileName));

            foreach (var mcu in MCUs)
            {
                var compatibleScripts = linkerScripts.Where(s => s.IsCompatibleWith(mcu)).OrderBy(s => s.SortOrder).ToArray();
                if (compatibleScripts.Length == 0)
                    throw new Exception("No linker scripts for " + mcu.Name);

                var stMCU = (DeviceListProviders.CubeProvider.STM32MCUBuilder)mcu;
                stMCU.DiscoveredLinkerScripts = compatibleScripts.Select(s => s.ToSuggestion()).ToArray();

                mcu.LinkerScriptPath = $"$$SYS:BSP_ROOT$$/{FamilyFilePrefix}LinkerScripts/$$com.sysprogs.stm32.memory_layout$$.ld";
            }

            return null;
        }

    }
}
