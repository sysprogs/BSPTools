/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using BSPEngine;
using BSPGenerationTools;
using LinkerScriptGenerator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CC3200_bsp_generator
{
    class Program
    {
        class CC3200BSPBuilder : BSPBuilder
        {
            const uint SRAMBase = 0x20004000;

            public CC3200BSPBuilder(BSPDirectories dirs)
                : base(dirs)
            {
                ShortName = "CC3200";
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = 0;
                ramBase = SRAMBase;
            }

            public override MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                //No additional memory information available for this MCU. Build a basic memory layout from known RAM/FLASH sizes.
                MemoryLayout layout = new MemoryLayout { DeviceName = mcu.Name, Memories = new List<Memory>() };

                layout.Memories.Add(new Memory
                {
                    Name = "SRAM",
                    Access = MemoryAccess.Undefined,
                    Type = MemoryType.RAM,
                    Start = SRAMBase,
                    Size = (uint)mcu.RAMSize,
                });

                return layout;
            }
        }

        static List<Framework> GenereteAddFrameWorks(BSPDirectories pBspDir, string pstrFile)
        {
            List<Framework> bleFrameworks = new List<Framework>();
            foreach (var line in File.ReadAllLines(Path.Combine(pBspDir.RulesDir, pstrFile)))// bspBuilder.Directories.RulesDir + @"\EmbFrameworks.txt"))
            {
                int idx = line.IndexOf('|');
                string dir = line.Substring(0, idx);
                string desc = line.Substring(idx + 1);
                string id = Path.GetFileName(dir);

                List<string> strRFrameworks = new List<string>();
                if (dir.StartsWith("smtp") || dir.StartsWith("http/server") || dir.StartsWith("xmpp"))
                    strRFrameworks.Add("com.sysprogs.arm.ti.cc3200.netapps.base64encoder");

                id = dir.Replace("/", "_");

                Patch[] aPatchesFilesFramowirks = null;
                string[] aStrIns = { "#include <stdlib.h>", "#define esnprintf snprintf" };
                string[] aStrInsiFn = { "#if 0" };
                string[] aStrInsiFnLe = { "#endif" };
                string[] aStrInsInT = { " #include <sys/types.h>" };
                string[] aStrInsLPrintN = { "#define Log_print1(...)",
                                            "#define Log_print2(...)",
                                            "#define Log_print3(...)",
                                            "#define Log_print4(...)",
                                            "#define Log_print5(...)",
                                            "#define Log_print6(...)",
                                            "#define Log_print0(...)"};

                if (dir.StartsWith("http/client"))
                    aPatchesFilesFramowirks = new Patch[]
                        {
#region JOB_PATCHES_FILES
                            new Patch.ReplaceLine
                                {
                                    FilePath = "httpsrv.h",
                                    OldLine = "#include <ti/net/network.h>",
                                    NewLine = "#include <network.h>",
                                },
                           new Patch.ReplaceLine
                                {
                                    FilePath = "httpsrv.c",
                                    OldLine = "#include <ti/net/network.h>",
                                    NewLine = "#include <network.h>",
                                },
                           new Patch.ReplaceLine
                                {
                                    FilePath = "ssnull.c",
                                    OldLine = "#include <ti/net/network.h>",
                                    NewLine = "#include <network.h>",
                                },
                           new Patch.ReplaceLine
                                {
                                    FilePath = "httpsend.c",
                                    OldLine = "#include <ti/net/http/httpsrv.h>",
                                    NewLine = "#include \"httpsrv.h\"",
                                },
                           new Patch.ReplaceLine
                                {
                                    FilePath = "httpsend.c",
                                    OldLine = "#error \"undefined printf() macros\"",
                                    NewLine = "//#error \"undefined printf() macros\"",

                                },
                           new Patch.InsertLines
                               {
                                   FilePath = "httpsend.c",
                                   AfterLine = "//#error \"undefined printf() macros\"",
                                   InsertedLines = aStrIns,
                               },
                            new Patch.ReplaceLine
                                {
                                    FilePath = "httpsrv.c",
                                    OldLine = "Registry_Desc ti_net_http_HTTPSrv_desc;",
                                    NewLine = "//Registry_Desc ti_net_http_HTTPSrv_desc;",
                                },
                            new Patch.ReplaceLine
                                {
                                    FilePath = "httpsrv.c",
                                    OldLine = "#include <ti/net/http/ssock.h>",
                                    NewLine = "#include \"ssock.h\"",
                                },
                            new Patch.ReplaceLine
                                {
                                    FilePath = "httpsrv.c",
                                    OldLine = "#include <ti/net/http/urlhandler.h>",
                                    NewLine = "#include \"urlhandler.h\"",
                                },
                            new Patch.ReplaceLine
                                {
                                    FilePath = "httpsrv.c",
                                    OldLine = "        Registry_addModule(&ti_net_http_HTTPSrv_desc, \"ti.net.http.HTTPSrv\");",
                                    NewLine = "      //Registry_addModule(&ti_net_http_HTTPSrv_desc, \"ti.net.http.HTTPSrv\");",
                                },
                            //------urlfile.h
                              new Patch.ReplaceLine
                                {
                                    FilePath = "httpsrv.h",
                                    OldLine = "#include <ti/net/http/ssock.h>",
                                    NewLine = "#include \"ssock.h\"",
                                },
                               new Patch.ReplaceLine
                                {
                                    FilePath = "httpsrv.h",
                                    OldLine = "#include <ti/net/http/urlhandler.h>",
                                    NewLine = "#include \"urlhandler.h\"",
                                },
                            //--- logging.h
                              new Patch.InsertLines
                               {
                                   FilePath = "logging.h",
                                   AfterLine = "#define _LOGGING_H_",
                                   InsertedLines = aStrInsiFn,
                               },
                              new Patch.InsertLines
                               {
                                   FilePath = "logging.h",
                                   AfterLine = "extern Registry_Desc ti_net_http_HTTPSrv_desc;",
                                   InsertedLines = aStrInsiFnLe,
                               },
                             new Patch.InsertLines
                               {
                                   FilePath = "logging.h",
                                   AfterLine = "#endif",
                                   InsertedLines = aStrInsLPrintN,
                               },
                            ///------------------ network.h
                              new Patch.InsertLines
                               {
                                   FilePath = "network.h",
                                   AfterLine = "#include <simplelink.h>",
                                   InsertedLines = aStrInsInT,
                               },
                              new Patch.ReplaceLine
                                {
                                    FilePath = "network.h",
                                    OldLine = "typedef long int ssize_t;",
                                    NewLine = "//typedef long int ssize_t;",
                                },
                          //------urlfile.h
                           new Patch.ReplaceLine
                                {
                                    FilePath = "urlfile.h",
                                    OldLine = "#include <ti/net/http/ssock.h>",
                                    NewLine = "#include \"ssock.h\"",
                                },
                            new Patch.ReplaceLine
                                {
                                    FilePath = "urlfile.h",
                                    OldLine = "#include <ti/net/http/urlhandler.h>",
                                    NewLine = "#include \"urlhandler.h\"",
                                },
#endregion JOB_PATCHES_FILES
                        };

                bleFrameworks.Add(new Framework
                {
                    Name = string.Format("Net Apps - {0} ({1})", desc, Path.GetFileName(dir)),
                    ID = "com.sysprogs.arm.ti.cc3200.netapps." + id,
                    ClassID = "com.sysprogs.arm.ti.cc3200.cl.netapps." + id,
                    ProjectFolderName = "netapps_" + dir,
                    DefaultEnabled = false,
                    RequiredFrameworks = strRFrameworks.ToArray(),

                    CopyJobs = new CopyJob[]
                        {
                            new CopyJob
                            {
                                SourceFolder = @"$$BSPGEN:INPUT_DIR$$\cc3200-sdk\netapps\" + dir,
                                TargetFolder = @"netapps\" + dir,
                                FilesToCopy = "-*base64.c;*.c;*.h",
                                Patches = aPatchesFilesFramowirks,
                            }
                        }
                });
            }

            return bleFrameworks;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: cc3200.exe <cc3200 SW package directory>");

            var bspBuilder = new CC3200BSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"));

            List<string> additionalSources = new List<string> { "$$SYS:BSP_ROOT$$/StartupFiles/startup_gcc.c" };

            MCUFamily fam = new MCUFamily
            {
                ID = "CC3200",
                UserFriendlyName = "CC3200",
                CompilationFlags = new ToolFlags
                {
                    PreprocessorMacros = new string[] { "gcc" },
                    IncludeDirectories = new string[] {"$$SYS:BSP_ROOT$$/SDK/driverlib",
                                                        "$$SYS:BSP_ROOT$$/SDK/inc",
                                                        "$$SYS:BSP_ROOT$$/SDK",
                                                        "$$SYS:BSP_ROOT$$/common",
                                                        "$$SYS:BSP_ROOT$$/SDK/oslib",
                                                        "$$SYS:BSP_ROOT$$/netapps",
                                                        "$$SYS:BSP_ROOT$$/SDK/simplelink",
                                                        "$$SYS:BSP_ROOT$$/SDK/simplelink/include",
                                                        "$$SYS:BSP_ROOT$$/SDK/simplelink_extlib/provisioninglib",
                                                        "."
                                                        },
                    COMMONFLAGS = "-mcpu=cortex-m4 -mthumb",
                },
                AdditionalSourceFiles = additionalSources.ToArray(),
            };

            fam.ConfigurableProperties = new PropertyList();
            List<MCUFamily> familyDefinitions = new List<MCUFamily>();
            familyDefinitions.Add(fam);

            List<MCU> mcuDefinitions = new List<MCU>();
            mcuDefinitions.Add(new MCU { FamilyID = "CC3200", ID = "XCC3200JR", RAMBase = 0x20004000, RAMSize = 240 * 1024, CompilationFlags = new ToolFlags { LinkerScript = "$$SYS:BSP_ROOT$$/LinkerScripts/XCC3200JR.lds" }, HierarchicalPath = @"TI ARM\CC3200" });
            mcuDefinitions.Add(new MCU { FamilyID = "CC3200", ID = "XCC3200HZ", RAMBase = 0x20004000, RAMSize = 176 * 1024, CompilationFlags = new ToolFlags { LinkerScript = "$$SYS:BSP_ROOT$$/LinkerScripts/XCC3200HZ.lds" }, HierarchicalPath = @"TI ARM\CC3200" });

            List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
            List<string> exampleDirs = new List<string>();

            var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));

            //Embedded Frameworks
            var AddFrW = GenereteAddFrameWorks(bspBuilder.Directories, "EmbFrameworks.txt");
            commonPseudofamily.Definition.AdditionalFrameworks = commonPseudofamily.Definition.AdditionalFrameworks.Concat(AddFrW).ToArray();

            foreach (var fw in commonPseudofamily.GenerateFrameworkDefinitions())
                frameworks.Add(fw);

            var flags = new ToolFlags();
            List<string> projectFiles = new List<string>();
            commonPseudofamily.CopyFamilyFiles(ref flags, projectFiles);

            foreach (var sample in commonPseudofamily.CopySamples())
                exampleDirs.Add(sample);

            BoardSupportPackage bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.arm.ti.cc3200",
                PackageDescription = "TI CC3200 Devices",
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "cc3200.mak",
                MCUFamilies = familyDefinitions.ToArray(),
                SupportedMCUs = mcuDefinitions.ToArray(),
                Frameworks = frameworks.ToArray(),
                Examples = exampleDirs.ToArray(),
                FileConditions = bspBuilder.MatchedFileConditions.ToArray(),
                PackageVersion = "1.2"
            };
            bspBuilder.Save(bsp, true);
        }
    }
}
