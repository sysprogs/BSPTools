using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KSDK2xImporter
{
    //This is based on BSPGeneratorTools.cs and should be updated from it to support newer cores
    class CoreFlagHelper
    {
        public enum CortexCore
        {
            Invalid,
            M0,
            M0Plus,
            M3,
            M33,
            M33_FPU,
            M4,
            M4_NOFPU,
            M7,
            A7,
            R5F,
        }

        [Flags]
        public enum CoreSpecificFlags
        {
            None = 0,
            FPU = 0x01,
            DefaultHardFloat = 0x02,
        }

        internal static void AddCoreSpecificFlags(CoreSpecificFlags flagsToDefine, MCUFamily family, CortexCore core)
        {
            //WARNING: If the proper

            string coreName = null, freertosPort = null;
            switch (core)
            {
                case CortexCore.M0:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m0 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM0" };
                    freertosPort = "ARM_CM0";
                    coreName = "M0";
                    break;
                case CortexCore.M0Plus:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m0plus -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM0PLUS" };
                    freertosPort = "ARM_CM0";
                    coreName = "M0";
                    break;
                case CortexCore.M3:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m3 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM3" };
                    coreName = "M3";
                    freertosPort = "ARM_CM3";
                    break;
                case CortexCore.M33:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m33 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM33" };
                    coreName = "M33";
                    freertosPort = "ARM_CM33_NTZ/non_secure";
                    break;
                case CortexCore.M33_FPU:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m33 -mthumb -mfpu=fpv5-sp-d16";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM33" };
                    coreName = "M33";
                    freertosPort = "ARM_CM33_NTZ/non_secure";
                    break;
                case CortexCore.M4:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m4 -mthumb -mfpu=fpv4-sp-d16";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM4" };
                    freertosPort = "ARM_CM4F";
                    coreName = "M4";
                    break;
                case CortexCore.M4_NOFPU:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m4 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM4" };
                    coreName = "M4";
                    freertosPort = "ARM_CM3";
                    break;
                case CortexCore.M7:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m7 -mthumb -mfpu=fpv5-sp-d16";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM7" };
                    coreName = "M7";
                    freertosPort = "ARM_CM7/r0p1";
                    break;
                case CortexCore.R5F:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-r5 -mfpu=vfpv3-d16 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CR5" };
                    break;
                default:
                    return;
            }

            if ((flagsToDefine & CoreSpecificFlags.FPU) == CoreSpecificFlags.FPU)
            {
                if (core == CortexCore.M4 || core == CortexCore.M7 || core == CortexCore.R5F || core == CortexCore.M33_FPU)
                {
                    AddFPModeProperty(flagsToDefine, family);
                }
            }

            List<SysVarEntry> vars = new List<SysVarEntry>();

            if (coreName != null)
                vars.Add(new SysVarEntry { Key = "com.sysprogs.bspoptions.arm.core", Value = coreName });
            if (freertosPort != null)
                vars.Add(new SysVarEntry { Key = "com.sysprogs.freertos.default_port", Value = freertosPort });

            if (vars.Count > 0)
                family.AdditionalSystemVars = LoadedBSP.Combine(family.AdditionalSystemVars, vars.ToArray());
        }

        public static void AddFPModeProperty(CoreSpecificFlags flagsToDefine, MCUFamily family)
        {
            if (family.ConfigurableProperties == null)
                family.ConfigurableProperties = new PropertyList { PropertyGroups = new List<PropertyGroup> { new PropertyGroup() } };
            family.ConfigurableProperties.PropertyGroups[0].Properties.Add(
                new PropertyEntry.Enumerated
                {
                    Name = "Floating point support",
                    UniqueID = "com.sysprogs.bspoptions.arm.floatmode",
                    SuggestionList = new PropertyEntry.Enumerated.Suggestion[]
                                {
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "-mfloat-abi=soft", UserFriendlyName = "Software"},
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "-mfloat-abi=hard", UserFriendlyName = "Hardware"},
                                                new PropertyEntry.Enumerated.Suggestion{InternalValue = "", UserFriendlyName = "Unspecified"},
                                },
                    DefaultEntryIndex = ((flagsToDefine & CoreSpecificFlags.DefaultHardFloat) == CoreSpecificFlags.DefaultHardFloat) ? 1 : 0,
                });

            family.CompilationFlags.COMMONFLAGS += " $$com.sysprogs.bspoptions.arm.floatmode$$";
        }

        public static void AddCoreSpecificFlags(CoreSpecificFlags flagsToDefine, MCUFamily family, string core)
        {
            CortexCore translatedCore = core switch {
                "cm0plus" => CortexCore.M0Plus,
                "cm0" => CortexCore.M0,
                "cm3" => CortexCore.M3,
                "cm33" => CortexCore.M33_FPU,
                "cm33_nodsp" => CortexCore.M33,
                "cm4" => CortexCore.M4,
                "cm7" => CortexCore.M7,
                _ => CortexCore.Invalid
            };

            AddCoreSpecificFlags(flagsToDefine, family, translatedCore);
        }
    }
}
