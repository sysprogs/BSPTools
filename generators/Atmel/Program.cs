/* Copyright (c) 2016 Sysprogs OU. All Rights Reserved.
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
using System.Text.RegularExpressions;


namespace Atmel_bsp_generator
{
    class Program
    {
        class AtmelBSPBuilder : BSPBuilder
        {
            const uint FLASHBase = 0x00000000, SRAMBase = 0x20000000;

            public AtmelBSPBuilder(BSPDirectories dirs)
                : base(dirs)
            {
                ShortName = "Atmel";

                LDSTemplate.SymbolAliases = new[]
                {
                    new SymbolAlias{Name = "__ram_end__", Target = "_edata"},
                    new SymbolAlias{Name = "_end", Target = "end"},
                };
            }

            public override string GetMCUTypeMacro(MCUBuilder mcu)
            {
                return $"__{mcu.Name.Substring(2)}__";
            }


            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = FLASHBase;
                ramBase = SRAMBase;
            }

            public override MemoryLayoutAndSubstitutionRules GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                //No additional memory information available for this MCU. Build a basic memory layout from known RAM/FLASH sizes.
                MemoryLayout layout = new MemoryLayout();
                layout.Memories = new List<Memory>();
                layout.DeviceName = mcu.Name;
                string shName = mcu.Name;
                string aDirSam;
                if (mcu.Name.StartsWith("AT"))
                    shName = mcu.Name.Substring(2, mcu.Name.Length - 2);
                if (shName.StartsWith("SAMC") || shName.StartsWith("SAMD") || shName.StartsWith("SAMR") || shName.StartsWith("SAMB") || shName.StartsWith("SAML"))
                    aDirSam = "\\Sam0";
                else
                    aDirSam = "\\Sam";
                //  EAK                if (shName == "SAMD10D13A" || shName == "SAMD10D14A" || shName == "SAMD10D12A"|| shName == "SAMD11D14A")
                //  EAK                    shName = shName + "S";//Different Device ID (DID)
                string aFileName = family.BSP.Directories.InputDir + aDirSam + "\\Utils\\Cmsis\\" + family.FamilyFilePrefix + "\\Include\\" + shName;

                if (family.FamilyFilePrefix.ToUpper().StartsWith("SAMG"))
                    aFileName = family.BSP.Directories.InputDir + "\\Sam\\Utils\\Cmsis\\samg\\" + family.FamilyFilePrefix + "\\Include\\" + shName;

                if (family.Definition.Name.ToUpper() == "SAM4C" || family.Definition.Name.ToUpper() == "SAM4CM" || family.Definition.Name.ToUpper() == "SAM4CP" || family.Definition.Name.ToUpper() == "SAM4CM32")
                    aFileName = aFileName + "_0.h";
                else
                    aFileName = aFileName + ".h";

                int RAMStart = -1;
                int FLASHStart = -1;

                if (Regex.IsMatch(mcu.Name, "SAML21...B"))
                    aFileName = aFileName.Replace("\\Include\\S", "\\Include_b\\S");

                foreach (var ln in File.ReadAllLines(aFileName))
                {
                    //                    var m = Regex.Match(ln, @"(#define [IH]?[HS]?[HMC]*RAM[C]?[0]?_ADDR[ \t]*.*0x)([0-9A-Fa-f]+[^uU]+)+.*");
                    var m = Regex.Match(ln, @"(#define [ID]*[IH]?[HS]?[HMC]*RAM[C]?[0]?_ADDR[ \t]*.*0x)([0-9A-Fa-f]+[^uU]+[L]?)+.*");

                    if (m.Success)
                    {
                        RAMStart = Convert.ToInt32(m.Groups[2].Value, 16);
                    }

                    m = Regex.Match(ln, @"(#define [I]?FLASH[0]?_ADDR[ \t]*.*0x)([0-9A-Fa-f]+[^u|U]+)+.*");
                    if (!m.Success)
                        m = Regex.Match(ln, @"(#define [I]?FLASH[0]?_CNC_ADDR[ \t]*.*0x)([0-9A-Fa-f]+[^u|U]+)+.*");
                    if (!m.Success)
                        m = Regex.Match(ln, @"(#define BOOTROM_ADDR[ \t]*.*0x)([0-9A-Fa-f]+[^u|U]+)+.*");//samb11
                    if (m.Success)
                    {
                        FLASHStart = Convert.ToInt32(m.Groups[2].Value, 16);
                    }
                    if (FLASHStart > -1 && RAMStart > -1)
                        break;
                }
                if (RAMStart == -1)
                    throw new Exception("no RAM Start");
                //    Console.WriteLine("RAMBase mcu {0} NO file :{1} ", mcu.Name, aFileName);
                if (FLASHStart == -1)
                    throw new Exception("no FLASH Start");
                //   Console.WriteLine("FLASHBase mcu {0} NO file :{1} ",mcu.Name , aFileName);
                layout.Memories.Insert(0, new Memory
                {
                    Name = "FLASH",
                    Access = MemoryAccess.Undefined,
                    Type = MemoryType.FLASH,
                    Start = (uint)FLASHStart,
                    Size = (uint)mcu.FlashSize
                });
                layout.Memories.Insert(0, new Memory
                {
                    Name = "SRAM",
                    Access = MemoryAccess.Undefined,
                    Type = MemoryType.RAM,
                    Start = (uint)RAMStart,
                    Size = (uint)mcu.RAMSize
                });

                return new MemoryLayoutAndSubstitutionRules(layout);
            }
        }


        static IEnumerable<StartupFileGenerator.InterruptVectorTable> ParseStartupFiles(string startupFileName, MCUFamilyBuilder fam)
        {
            string aStartNewName;
            string strPrefFam;
            if (fam.Definition.Name.StartsWith("AT"))
                strPrefFam = fam.Definition.Name.Substring(2, fam.Definition.Name.Length - 2);
            else
                strPrefFam = fam.Definition.Name;
            if (strPrefFam != "SAMG51")
                strPrefFam = strPrefFam.Substring(0, strPrefFam.Length - 1);

            if (strPrefFam == "SAM4cm3")
                strPrefFam = strPrefFam.Substring(0, strPrefFam.Length - 1);

            foreach (var fl in Directory.GetFiles(Path.GetDirectoryName(startupFileName), strPrefFam + "*.h", SearchOption.AllDirectories/* TopDirectoryOnly*/))
            {
                if (fl.Contains("\\pio\\"))
                    continue;
                string aNameFl = fl.Substring(fl.LastIndexOf("\\") + 1, fl.Length - fl.LastIndexOf("\\") - 1);
                if (Path.GetFileNameWithoutExtension(aNameFl).ToUpper() == strPrefFam.ToUpper())
                    continue;
                if (aNameFl.EndsWith("_1.h"))
                    continue;
                if (aNameFl.EndsWith("_0.h"))
                    aNameFl = aNameFl.Replace("_0.h", ".h");

                aStartNewName = "startup_" + aNameFl;

                List<StartupFileGenerator.InterruptVector[]> list = new List<StartupFileGenerator.InterruptVector[]>();
                list.Add(StartupFileGenerator.ParseInterruptVectors(fl,
                         @"^typedef struct _DeviceVectors.*",
                         @"} DeviceVectors;.*",
                         @"[ \t]+void\*[ ]+([\w]+).*",
                         @"([^ \t,]+)[,]?.*",
                         @"^[ \t]*[/{]+.*",
                         null,
                         1,
                         2));
                List<StartupFileGenerator.InterruptVector> vectors = new List<StartupFileGenerator.InterruptVector>(list[0]);
                list.RemoveAt(0);

                //Fix the vector names from comments
                for (int i = 0; i < vectors.Count; i++)
                {
                    if (vectors[i] == null)
                        continue;

                    if (i == 0)
                    {
                        vectors[i].Name = "_estack";
                        continue;
                    }
                    else if (i == 1)
                    {
                        vectors[i].Name = "Reset_Handler";
                        continue;
                    }
                    else if (vectors[i].Name.StartsWith("pvReserved"))
                    {
                        vectors[i] = null;
                        continue;
                    }
                    else
                    {
                        if (vectors[i] == null)
                            continue;

                        if (!vectors[i].Name.StartsWith("pfn"))
                            throw new Exception("no pfn Func Startup Header");

                        vectors[i].Name = vectors[i].Name.Substring(3, vectors[i].Name.Length - 3);
                    }

                }
                yield return new StartupFileGenerator.InterruptVectorTable
                {
                    FileName = Path.ChangeExtension(Path.GetFileName(aStartNewName), ".c"),
                    MatchPredicate = m => (Path.GetFileNameWithoutExtension(aNameFl).ToUpper() == m.Name.Substring(2).ToUpper()),
                    Vectors = vectors.ToArray()
                };
            }
        }

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir, MCUFamilyBuilder fam)
        {
            List<MCUDefinitionWithPredicate> RegistersPeriphs = new List<MCUDefinitionWithPredicate>();
            Dictionary<string, HardwareRegisterSet[]> periphs = PeripheralRegisterGenerator.GenerateFamilyPeripheralRegistersAtmel(dir + "\\" + fam.Definition.FamilySubdirectory + "\\Utils\\Include", fam.FamilyFilePrefix);
            //      Dictionary<string, HardwareRegisterSet[]> periphs;
            if (fam.FamilyFilePrefix.Contains("SAMl21"))
                periphs = periphs.Concat(PeripheralRegisterGenerator.GenerateFamilyPeripheralRegistersAtmel(dir + "\\" + fam.Definition.FamilySubdirectory + "\\Utils\\Include_b", fam.FamilyFilePrefix)).ToDictionary(v => v.Key, v => v.Value);
            foreach (var subfamily in periphs.Keys)
            {
                MCUDefinitionWithPredicate mcu_def = new MCUDefinitionWithPredicate { MCUName = subfamily, RegisterSets = periphs[subfamily], MatchPredicate = m => (subfamily == m.Name.Substring(2)), };
                RegistersPeriphs.Add(mcu_def);
            }
            return RegistersPeriphs;
        }

        static List<MCUBuilder> RemoveDuplicateMCU(ref List<MCUBuilder> rawmcu_list)
        {
            foreach (var amcu in rawmcu_list)
            {
                if (!amcu.Name.StartsWith("AT"))
                {
                    if (amcu.Name.StartsWith("SAM"))
                        amcu.Name = "AT" + amcu.Name;
                    else
                        throw new Exception("Unexpected MCU name");
                }
                var idx = amcu.Name.IndexOf("-");
                if (idx > 0)
                    amcu.Name = amcu.Name.Remove(idx);
            }
            return rawmcu_list;
        }
        static List<Framework> GenereteAddFrameWorks(BSPDirectories pBspDir, string pstrFile)
        {
            List<Framework> bleFrameworks = new List<Framework>();
            List<PropertyEntry> propFr = new List<PropertyEntry>();
            PropertyList pl = new PropertyList();

            List<string> a_SimpleFileConditions = new List<string>();
            foreach (var line in File.ReadAllLines(Path.Combine(pBspDir.RulesDir, pstrFile)))
            {
                string dir = line;
                string desc = "";
                string id = dir;

                int idx = line.IndexOf('|');
                if (idx > 0)
                {
                    dir = line.Substring(0, idx);
                    desc = line.Substring(idx + 1);
                    id = Path.GetFileName(dir);
                }
                dir = dir.ToLower();

                id = dir.Replace("\\", "_");
                string a_name = line.Substring(line.LastIndexOf("\\") + 1).ToLower(); ;


                dir = a_name;
                string aIncludeMask = "-sam[0-9g]?*;-uc*;*.h";
                a_SimpleFileConditions.Clear();

                if (a_name.ToLower() == "sleepmgr")
                {
                    a_SimpleFileConditions.Add(@"samd\\*: $$com.sysprogs.atmel.sam32._header_prefix_samser$$ == samd");
                    a_SimpleFileConditions.Add(@"samc\\*: $$com.sysprogs.atmel.sam32._header_prefix_samser$$ == samc");
                    a_SimpleFileConditions.Add(@"sam\\*: $$com.sysprogs.atmel.sam32._header_prefix_sam0$$ != yes");
                    aIncludeMask = @"-*/*;*.h";
                }
                if (a_name.ToLower() == "serial")
                {
                    a_SimpleFileConditions.Add(@"sam0_usart\\*: $$com.sysprogs.atmel.sam32._header_prefix_sam0$$ == yes");
                    a_SimpleFileConditions.Add(@"sam_uart\\*: $$com.sysprogs.atmel.sam32._header_prefix_sam0$$ != yes");
                    a_SimpleFileConditions.Add(@"samb_uart\\*: $$com.sysprogs.atmel.sam32._header_prefix_sam0$$ == samb");
                    aIncludeMask = @"-*/*;*.h";
                }
                if (a_name.ToLower() == "adp" || a_name.ToLower() == "ioport")
                {
                    a_SimpleFileConditions.Add(@"sam0\\*: $$com.sysprogs.atmel.sam32._header_prefix_sam0$$ == yes");
                    a_SimpleFileConditions.Add(@"sam\\*: $$com.sysprogs.atmel.sam32._header_prefix_sam0$$ != yes");
                    aIncludeMask = @"-*/*;*.h";
                }


                if (a_name.ToLower() == "clock")
                {
                    a_SimpleFileConditions.Add(@"sam4l\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sam4l");
                    a_SimpleFileConditions.Add(@"sam3n\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sam3n");
                    a_SimpleFileConditions.Add(@"sam3s\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sam3s");
                    a_SimpleFileConditions.Add(@"sam3u\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sam3u");
                    a_SimpleFileConditions.Add(@"sam3x\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sam3x");
                    a_SimpleFileConditions.Add(@"sam4c\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sam4c");
                    a_SimpleFileConditions.Add(@"sam4cp\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sam4cp");
                    a_SimpleFileConditions.Add(@"sam4cm\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sam4cm");
                    a_SimpleFileConditions.Add(@"sam4e\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sam4e");
                    a_SimpleFileConditions.Add(@"sam4n\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sam4n");
                    a_SimpleFileConditions.Add(@"sam4s\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sam4s");
                    a_SimpleFileConditions.Add(@"same70\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == same70");
                    a_SimpleFileConditions.Add(@"sams70\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == sams70");
                    a_SimpleFileConditions.Add(@"samv70\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == samv70");
                    a_SimpleFileConditions.Add(@"samv71\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == samv71");
                    a_SimpleFileConditions.Add(@"samg\\sysclk.c: $$com.sysprogs.atmel.sam32._header_prefix$$ == samg51");

                    aIncludeMask = @"-*/*;*.h";
                }

                bleFrameworks.Add(new Framework
                {
                    Name = "Atmel Services " + dir,
                    ID = "com.sysprogs.arm.atmel.service." + dir,
                    ClassID = "com.sysprogs.arm.atmel.cl.services." + dir,
                    ProjectFolderName = "Services_" + dir,
                    DefaultEnabled = false,

                    CopyJobs = new []
                    {
                        new CopyJob
                        {
                            SourceFolder = @"$$BSPGEN:INPUT_DIR$$\common\services\"+dir,
                            TargetFolder = @"common\services\"+dir,
                            FilesToCopy = "-*example*.c;-*example*.h;-*unit_test*.*;-*doxygen*;-*mega*;-*avr*;-*uc3*;*.c;*.h",
                            AutoIncludeMask =aIncludeMask,
                            ProjectInclusionMask = "*.c",
                            SimpleFileConditions = a_SimpleFileConditions.ToArray(),
                        }
                    },
                });
            }
            return bleFrameworks;
        }
        static void SetDriverConditions(string[] strPropFrim, ref List<PropertyEntry> propFr, ref List<string> a_SimpleFileConditions, ref string aInclJob, string pstrTypSam)
        {
            string a_macname = "com.sysprogs.arm.atmel.drivers." + pstrTypSam + ".";
            bool flSercom = false;
            foreach (var line in strPropFrim)
            {

                //  Debug
                /*  if (!line.Contains("spi") && !line.Contains("sercom") && !line.Contains("i2c") && !line.Contains("uart"))
                        continue;
                 */
                if (line.EndsWith(@"sercom\spi_master_vec"))
                    continue;
                string addContinB11 = "$$com.sysprogs.atmel.sam32._header_prefix$$!=samb11";

                if (line.EndsWith("sercom"))
                {
                    string[] strFilesSercom = Directory.GetFiles(line, "*.c", SearchOption.TopDirectoryOnly);// Path.Combine(pBspDir.InputDir, pstrTypSam + @"\drivers\sercom"));

                    foreach (var fl in strFilesSercom)
                        a_SimpleFileConditions.Add("^sercom\\\\" + Path.GetFileName(fl) + @":" + addContinB11);

                    string[] strPropFrimSercom = Directory.GetDirectories(line);// Path.Combine(pBspDir.InputDir, pstrTypSam + @"\drivers\sercom"));
                    SetDriverConditions(strPropFrimSercom, ref propFr, ref a_SimpleFileConditions, ref aInclJob, pstrTypSam);
                    continue;
                }
                bool aflIsCond = false;
                string addPatch = "";
                string aAddSimpleCond = "";
                string a_name = line.Substring(line.LastIndexOf("\\") + 1).ToLower();
                addContinB11 = " && $$com.sysprogs.atmel.sam32._header_prefix$$==samb11";
                if (line.Contains("sercom"))
                {
                    flSercom = true;
                    addContinB11 = " && $$com.sysprogs.atmel.sam32._header_prefix$$!=samb11";
                    addPatch = "sercom\\\\";
                }
                if (!line.EndsWith("\\spi") && !line.Contains("sercom") && !line.Contains("i2c")
                    && !line.Contains("uart") && !line.Contains("usart"))
                    addContinB11 = " ";

                if (a_name.Contains("gpio") && pstrTypSam.ToUpper() == "SAM")
                    aInclJob = "";

                if (Directory.GetFiles(line, "*.c", SearchOption.AllDirectories).Count() == 0)
                    continue;

                string[] strDirToDir = Directory.GetDirectories(line, "*", SearchOption.AllDirectories);
                //  string[] strDirToDir = Directory.GetFiles(line, "*", SearchOption.AllDirectories);
                //          string aDrivCond = "^" + a_name + @"\\: $$" + a_macname + a_name + "$$ == 1";

                string aDrivCond = " $$" + a_macname + a_name + "$$ == 1";
                if (a_name == "usart" && flSercom)
                    aDrivCond = " $$" + a_macname + "uart$$ == 1";// sercom/usart ->uart
                if (pstrTypSam.ToUpper() == "SAM0")
                {
                    foreach (var dir in strDirToDir)
                        if (dir.Contains("_sam"))
                        {
                            if (dir.Contains("quick"))
                                continue;
                            string aPatchPref = dir.Substring(dir.LastIndexOf(a_name + "\\") + a_name.Length + 1).ToLower();

                            string prefs = "^sam(";
                            if (dir.EndsWith("module_config"))
                                continue;
                            if (dir.Contains("unit_test"))
                                continue;
                            int ac = 0;
                            int idx1 = dir.LastIndexOf("\\");
                            int idx2 = dir.LastIndexOf("_sam");
                            string aflPtch;
                            if (idx2 < idx1)
                                aflPtch = dir.Remove(idx1);
                            else
                                aflPtch = dir;

                            foreach (var pref in aflPtch.Substring(dir.LastIndexOf("_sam") + 4).Split('_'))
                            {
                                if (pref == "")
                                    continue;
                                /*      int idx = pref.LastIndexOf("\\");
                                      string regdata;
                                      if (idx > 0)
                                          regdata = pref.Remove(idx);
                                      else*/
                                //   regdata = pref;
                                if (pref[0] != '0' && pref[0] != 'd' && pref[0] != 'r' && pref[0] != 'l' && pref[0] != 'b' && pref[0] != 'c' && pref[0] != 'h')
                                    throw new Exception(" unknow prefix " + pref + " in " + dir);
                                if (ac > 0)
                                    prefs += "|";

                                if (pref == "0")
                                    prefs = "sam(?!d20";
                                else
                                    prefs += pref;//.Replace(".c", "");



                                ac++;
                            }
                            prefs += ")";
                            aPatchPref = aPatchPref.Replace(@"\", @"\\");
                            /*            if(aPatchPref.EndsWith(".c"))//file
                                          aAddSimpleCond = "^" + a_name + @"\\" + aPatchPref + @":$$com.sysprogs.atmel.sam32._header_prefix$$ =~ " + prefs;
                                         else
                                */
                            aAddSimpleCond = "^" + addPatch + a_name + @"\\" + aPatchPref + @"\\:$$com.sysprogs.atmel.sam32._header_prefix$$ =~ " + prefs;

                            a_SimpleFileConditions.Add(aAddSimpleCond + "&& " + aDrivCond + addContinB11);


                            aflIsCond = true;

                        }
                    if (aflIsCond)
                    {
                        foreach (var dir in strDirToDir)
                            if (!dir.Contains("_sam"))
                            {
                                if (dir.Contains("quick"))
                                    continue;
                                if (dir.Contains("unit") || dir.Contains("docimg"))
                                    continue;
                                if (Directory.GetFiles(dir, "*.c", SearchOption.TopDirectoryOnly).Count() == 0)
                                    continue;

                                string aPatchPref = dir.Substring(dir.LastIndexOf(a_name + "\\") + a_name.Length + 1).ToLower();
                                aAddSimpleCond = "^" + addPatch + a_name + @"\\" + aPatchPref + @"\\:" + aDrivCond;
                                a_SimpleFileConditions.Add(aAddSimpleCond + addContinB11);
                            }
                        string[] strFileToDir = Directory.GetFiles(line, "*.c");
                        foreach (var file in strFileToDir)
                        {

                            if (Path.GetFileNameWithoutExtension(file).Contains("_sam_"))
                            {
                                string prefs = "sam(";
                                string aPatchPref = file.Substring(file.LastIndexOf(a_name + "\\") + a_name.Length + 1).ToLower();
                                foreach (var pref in Path.GetFileNameWithoutExtension(file).Substring(Path.GetFileNameWithoutExtension(file).LastIndexOf("_sam_") + 5).Split('_'))
                                {
                                    //   regdata = pref;
                                    if (pref[0] != '0' && pref[0] != 'd' && pref[0] != 'r' && pref[0] != 'l' && pref[0] != 'b' && pref[0] != 'c')
                                        throw new Exception(" unknow prefix " + pref + " in ");


                                    if (pref == "0")
                                        prefs = "sam(?!d20";
                                    else
                                        prefs += pref;//.Replace(".c", "");

                                }
                                prefs += ")";

                                aAddSimpleCond = "^" + addPatch + a_name + @"\\" + aPatchPref + @":$$com.sysprogs.atmel.sam32._header_prefix$$ =~ " + prefs;
                                a_SimpleFileConditions.Add(aAddSimpleCond + aDrivCond + addContinB11);
                                if (prefs == "sam()")
                                    a_SimpleFileConditions.Add(aAddSimpleCond);
                            }
                            else
                            {
                                aAddSimpleCond = "^" + addPatch + a_name + @"\\" + Path.GetFileName(file) + ":" + aDrivCond;
                                a_SimpleFileConditions.Add(aAddSimpleCond + addContinB11);
                            }

                        }
                    }
                }
                string a_nameFr = a_name;
                if (a_name == "usart" && flSercom)
                    a_nameFr = "uart";
                string uID = a_macname + a_nameFr;
                if (propFr.Where(p => p.UniqueID == uID).Count() == 0)
                    propFr.Add(new PropertyEntry.Boolean
                    {
                        Name = a_nameFr,
                        UniqueID = uID,
                        ValueForTrue = "1",
                        ValueForFalse = "0",
                    });
                if (!aflIsCond)
                    a_SimpleFileConditions.Add("^" + addPatch + a_name + @"\\:" + aDrivCond + addContinB11);

            }
        }
        static List<Framework> GenereteAddFrameWorksDir(BSPDirectories pBspDir, string pstrTypSam)
        {
            List<Framework> bleFrameworks = new List<Framework>();
            List<PropertyEntry> propFr = new List<PropertyEntry>();
            PropertyList pl = new PropertyList();

            List<string> a_SimpleFileConditions = new List<string>();

            string strIdFr = "com.sysprogs.arm.atmel.drivers." + pstrTypSam;
            string strClFr = "com.sysprogs.arm.atmel.cl.drivers." + pstrTypSam;
            string nameFr = pstrTypSam.ToUpper() + "_Drivers";
            string asrcJob = @"$$BSPGEN:INPUT_DIR$$\" + pstrTypSam + @"\drivers";

            string astrTargetJob = pstrTypSam + @"\drivers";
            string astrFileJob = "-*example*.c;-*example*.h;-*unit_test*.*;-*doxygen*;-*mega*;-*avr*;-*adc_enhanced_mode*;-*quick_start*;*.c;*.h";
            string aInclJob = "";// " - sam[0-9g]?*;-uc*;*.h";
            string aPrjInclMsk = "*.c;*.h";
            List<string> strIncompFrIDs = new List<string>();
            if (pstrTypSam.ToUpper() == "SAM")
                strIncompFrIDs.Add("com.sysprogs.arm.atmel.drivers.sam0");
            else
            {
                aPrjInclMsk = "-sercom\\spi_master_vec\\*.c;-*_sam*.h;*.c;*.h";

                aInclJob = "$$SYS:BSP_ROOT$$/sam0/drivers/system/clock/$$com.sysprogs.atmel.sam0.driver.clock$$;" +
                           "$$SYS:BSP_ROOT$$/sam0/drivers/system/clock/$$com.sysprogs.atmel.sam0.driver.clock$$/module_config;" +
                           "$$SYS:BSP_ROOT$$/sam0/drivers/system/interrupt/$$com.sysprogs.atmel.sam0.driver.interrupt$$;" +
                           "$$SYS:BSP_ROOT$$/sam0/drivers/system/interrupt/$$com.sysprogs.atmel.sam0.driver.interrupt$$/module_config;" +
                            "$$SYS:BSP_ROOT$$/sam0/drivers/system/power/$$com.sysprogs.atmel.sam0.driver.power$$;" +
                           "$$SYS:BSP_ROOT$$/sam0/drivers/system/reset/$$com.sysprogs.atmel.sam0.driver.reset$$" +
                           ";$$SYS:BSP_ROOT$$/sam0/drivers/adc/$$com.sysprogs.atmel.sam0.driver.globaldir$$";

                foreach (var dirdr in Directory.GetDirectories(Path.Combine(pBspDir.InputDir, pstrTypSam + @"\drivers")))
                    foreach (var dirdr1 in Directory.GetDirectories(dirdr))
                        if (dirdr1.Contains("_sam"))
                        {
                            string aName = dirdr.Substring(dirdr.LastIndexOf("\\") + 1);
                            aInclJob += @";$$SYS:BSP_ROOT$$/sam0/drivers/" + aName + @"/" + aName + "$$com.sysprogs.atmel.sam0.driver.globaldir$$";
                            break;

                        }
                strIncompFrIDs.Add("com.sysprogs.arm.atmel.drivers.sam");
            }

            string[] strPropFrim = Directory.GetDirectories(Path.Combine(pBspDir.InputDir, pstrTypSam + @"\drivers"));

            SetDriverConditions(strPropFrim, ref propFr, ref a_SimpleFileConditions, ref aInclJob, pstrTypSam);

            List<PropertyGroup> pg = new List<PropertyGroup>();
            pg.Add(new PropertyGroup { Name = nameFr, Properties = propFr });
            pl.PropertyGroups = pg;

            aInclJob = aInclJob.Trim(';');
            if (aInclJob == "")
                aInclJob = null;

            bleFrameworks.Add(new Framework
            {
                Name = nameFr + "Library",
                ID = strIdFr,
                ClassID = strClFr,
                ProjectFolderName = "Atmel_" + nameFr,
                DefaultEnabled = false,
                IncompatibleFrameworks = strIncompFrIDs.ToArray(),

                CopyJobs = new CopyJob[]
                       {
                            new CopyJob
                            {
                                PreprocessorMacros = "ADC_CALLBACK_MODE=true;USART_CALLBACK_MODE=true",
                                SourceFolder = asrcJob ,
                                TargetFolder = astrTargetJob ,
                                FilesToCopy = astrFileJob,
                                SimpleFileConditions = a_SimpleFileConditions.ToArray(),
                               AutoIncludeMask = "-*_sam*.h;*.h",
                              AdditionalIncludeDirs = aInclJob,
                                ProjectInclusionMask = aPrjInclMsk
                        }
                    },
                ConfigurableProperties = pl,

            });

            return bleFrameworks;
        }
        static void CopyAddSourceFiles(string pStartDir)
        {
            string aSourceFile = pStartDir + @"\sam\utils\cmsis\samg\samg54\include\component\component_twihs.h";
            string aTargetFile = pStartDir + @"\sam\utils\cmsis\samg\samg55\include\component\twihs.h";

            if (!File.Exists(aSourceFile))
                throw new Exception(aSourceFile + " Source file does not exist");

            if (!File.Exists(aTargetFile))
                File.Copy(aSourceFile, aTargetFile, true);
        }
        //----------------------------------------------
        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: EFM32.exe <Atmel SW package directory>");

            using (var bspBuilder = new AtmelBSPBuilder(BSPDirectories.MakeDefault(args)))
            {
                var devices = BSPGeneratorTools.ReadMCUDevicesFromCommaDelimitedCSVFile(bspBuilder.Directories.RulesDir + @"\McuAtmel.csv",
                    "Device Name", "Flash (kBytes)", "SRAM (kBytes)", "CPU", true);
                RemoveDuplicateMCU(ref devices);

                foreach(var mcu in devices)
                {
                    if (mcu.Core == CortexCore.M4)
                        mcu.FPU = FPUType.SP;
                }


                List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
                foreach (var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\Families", "*.xml"))
                    allFamilies.Add(new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(fn)));

                var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allFamilies);
                List<MCUFamily> familyDefinitions = new List<MCUFamily>();
                List<MCU> mcuDefinitions = new List<MCU>();
                List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();

                List<MCUFamilyBuilder.CopiedSample> exampleDirs = new List<MCUFamilyBuilder.CopiedSample>();

                CopyAddSourceFiles(bspBuilder.Directories.InputDir);

                bool noPeripheralRegisters = args.Contains("/noperiph");
                List<KeyValuePair<string, string>> macroToHeaderMap = new List<KeyValuePair<string, string>>();

                var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));

                //Embedded Frameworks
                var AddFrW = GenereteAddFrameWorks(bspBuilder.Directories, "ServicesFrimwork.txt");
                commonPseudofamily.Definition.AdditionalFrameworks = commonPseudofamily.Definition.AdditionalFrameworks.Concat(AddFrW).ToArray();
                AddFrW = GenereteAddFrameWorksDir(bspBuilder.Directories, "sam");
                commonPseudofamily.Definition.AdditionalFrameworks = commonPseudofamily.Definition.AdditionalFrameworks.Concat(AddFrW).ToArray();
                AddFrW = GenereteAddFrameWorksDir(bspBuilder.Directories, "sam0");
                commonPseudofamily.Definition.AdditionalFrameworks = commonPseudofamily.Definition.AdditionalFrameworks.Concat(AddFrW).ToArray();

                foreach (var fw in commonPseudofamily.GenerateFrameworkDefinitions())
                    frameworks.Add(fw);

                var flags = new ToolFlags();
                List<string> projectFiles = new List<string>();
                commonPseudofamily.CopyFamilyFiles(ref flags, projectFiles);

                foreach (var sample in commonPseudofamily.CopySamples())
                    exampleDirs.Add(sample);

                foreach (var fam in allFamilies)
                {
                    var rejectedMCUs = fam.RemoveUnsupportedMCUs();
                    if (rejectedMCUs.Length != 0)
                    {
                        Console.WriteLine("Unsupported {0} MCUs:", fam.Definition.Name);
                        foreach (var mcu in rejectedMCUs)
                            Console.WriteLine("\t{0}", mcu.Name);
                    }


                    fam.AttachStartupFiles(ParseStartupFiles(fam.Definition.StartupFileDir, fam));

                    var famObj = fam.GenerateFamilyObject(true);

                    famObj.AdditionalSourceFiles = LoadedBSP.Combine(famObj.AdditionalSourceFiles, projectFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).ToArray());
                    famObj.AdditionalHeaderFiles = LoadedBSP.Combine(famObj.AdditionalHeaderFiles, projectFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).ToArray());

                    famObj.AdditionalSystemVars = LoadedBSP.Combine(famObj.AdditionalSystemVars, commonPseudofamily.Definition.AdditionalSystemVars);
                    famObj.CompilationFlags = famObj.CompilationFlags.Merge(flags);
                    famObj.CompilationFlags.PreprocessorMacros = LoadedBSP.Combine(famObj.CompilationFlags.PreprocessorMacros, new string[] { "$$com.sysprogs.bspoptions.primary_memory$$_layout" });

                    familyDefinitions.Add(famObj);
                    var memoryLayouts = fam.GenerateLinkerScripts(false);
                    if (!noPeripheralRegisters)
                        fam.AttachPeripheralRegisters(ParsePeripheralRegisters(bspBuilder.Directories.OutputDir, fam));

                    foreach (var mcu in fam.MCUs)
                    {
                        var mcuDef = mcu.GenerateDefinition(fam, bspBuilder, !noPeripheralRegisters);
                        var layout = memoryLayouts[mcu.Name].Layout;

                        var ram = layout.Memories.First(m => m.Type == MemoryType.RAM);
                        var flash = layout.Memories.First(m => m.Type == MemoryType.FLASH);

                        mcuDef.RAMBase = ram.Start;
                        mcuDef.RAMSize = (int)ram.Size;

                        mcuDef.FLASHBase = flash.Start;
                        mcuDef.FLASHSize = (int)flash.Size;

                        mcuDefinitions.Add(mcuDef);
                    }

                    foreach (var fw in fam.GenerateFrameworkDefinitions())
                        frameworks.Add(fw);

                    foreach (var sample in fam.CopySamples())
                        exampleDirs.Add(sample);

                }

                BoardSupportPackage bsp = new BoardSupportPackage
                {
                    PackageID = "com.sysprogs.arm.atmel.sam-cortex",
                    PackageDescription = "Atmel ARM Cortex Devices",
                    GNUTargetID = "arm-eabi",
                    GeneratedMakFileName = "atmel.mak",
                    MCUFamilies = familyDefinitions.ToArray(),
                    SupportedMCUs = mcuDefinitions.ToArray(),
                    Frameworks = frameworks.ToArray(),
                    Examples = exampleDirs.Where(s => !s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),
                    TestExamples = exampleDirs.Where(s => s.IsTestProjectSample).Select(s => s.RelativePath).ToArray(),

                    FileConditions = bspBuilder.MatchedFileConditions.Values.ToArray(),
                    PackageVersion = "3.35.2R2"
                };

                bspBuilder.Save(bsp, true);
            }
        }
    }
}
