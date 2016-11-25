using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KSDK2xImporter
{
    class CoreFlagHelper
    {
        public enum CortexCore
        {
            Invalid,
            M0,
            M0Plus,
            M3,
            M4,
            M7,
        }

        public const string PrimaryMemoryOptionName = "com.sysprogs.bspoptions.primary_memory";

        internal static void AddCoreSpecificFlags(bool defineConfigurationVariables, MCUFamily family, CortexCore core)
        {
            string coreName = null;
            switch (core)
            {
                case CortexCore.M0:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m0 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM0" };
                    coreName = "M0";
                    break;
                case CortexCore.M0Plus:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m0plus -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM0PLUS" };
                    coreName = "M0";
                    break;
                case CortexCore.M3:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m3 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM3" };
                    coreName = "M3";
                    break;
                case CortexCore.M4:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m4 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM4" };
                    family.CompilationFlags.ASFLAGS = "-mfpu=fpv4-sp-d16";
                    coreName = "M4";
                    break;
                case CortexCore.M7:
                    family.CompilationFlags.COMMONFLAGS = "-mcpu=cortex-m7 -mthumb";
                    family.CompilationFlags.PreprocessorMacros = new string[] { "ARM_MATH_CM7" };
                    coreName = "M7";
                    break;
                default:
                    throw new Exception("Unsupported core type");
            }


            if (defineConfigurationVariables)
            {
                if (core != CortexCore.M0)
                {
                    family.ConfigurableProperties = new PropertyList
                    {
                        PropertyGroups = new List<PropertyGroup>
                            {
                                new PropertyGroup
                                {
                                    Properties = new List<PropertyEntry>
                                    {
                                    }
                                }
                            }
                    };

                    if (core == CortexCore.M4 || core == CortexCore.M7)
                    {
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
                                            }
                            });

                        family.CompilationFlags.COMMONFLAGS += " $$com.sysprogs.bspoptions.arm.floatmode$$";
                    }
                }

                if (coreName != null)
                    family.AdditionalSystemVars = LoadedBSP.Combine(family.AdditionalSystemVars, new SysVarEntry[] { new SysVarEntry { Key = "com.sysprogs.bspoptions.arm.core", Value = coreName } });
            }
        }

        public static void AddCoreSpecificFlags(bool defineConfigurationVariables, MCUFamily family, string core)
        {
            switch(core ?? "")
            {
                case "cm0":
                    AddCoreSpecificFlags(defineConfigurationVariables, family, CortexCore.M0);
                    break;
                case "cm3":
                    AddCoreSpecificFlags(defineConfigurationVariables, family, CortexCore.M3);
                    break;
                case "cm4":
                    AddCoreSpecificFlags(defineConfigurationVariables, family, CortexCore.M4);
                    break;
                case "cm7":
                    AddCoreSpecificFlags(defineConfigurationVariables, family, CortexCore.M7);
                    break;
            }
        }
    }
}
