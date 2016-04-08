/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/

using BSPEngine;
using BSPGenerationTools;
using LinkerScriptGenerator;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Collections.Specialized;

namespace Nxp_bsp_generator
{
    class Program
    {
        class NxpBSPBuilder : BSPBuilder
        {
            const uint FLASHBase = 0x00000000, SRAMBase = 0x10000000;
            private readonly Dictionary<string, List<Memory>> _Memories;

            public NxpBSPBuilder(BSPDirectories dirs)
                : base(dirs)
            {
                ShortName = "NXP_LPC";
                _Memories = ParseMemories(Path.Combine(dirs.RulesDir, "memories.csv"));
            }

            static Dictionary<string, List<Memory>> ParseMemories(string csvMemoriesFile)
            {
                Dictionary<string, List<Memory>> memories = new Dictionary<string, List<Memory>>();

                string[] ram_names = null;
                foreach (var line in File.ReadAllLines(csvMemoriesFile))
                {
                    if (line.Contains("Device Family"))
                    {
                        string[] headers = line.Split(new string[] { "," }, StringSplitOptions.None);
                        ram_names = new string[headers.Length / 2];

                        for (int i = 1; i < headers.Length; i += 2)
                        {
                            ram_names[(i - 1) / 2] = headers[i].Substring(0, headers[i].Length - " Address".Length).Replace(" ", "_");
                        }
                        continue;
                    }

                    string[] line_array = line.Split(new string[] { "," }, StringSplitOptions.None);
                    memories[line_array[0]] = new List<Memory>();
                    for (int i = 1; i < line_array.Length; i += 2)
                    {
                        if (line_array[i].Trim() == "")
                            continue;

                        memories[line_array[0]].Add(
                            new Memory()
                            {
                                Name = ram_names[(i - 1) / 2],
                                Start = UInt32.Parse(line_array[i].Substring(2).Replace(" ", ""), System.Globalization.NumberStyles.HexNumber),
                                Size = UInt32.Parse(line_array[i + 1]) * 1024,
                                Type = MemoryType.RAM
                            });
                    }

                    if (memories[line_array[0]].Count == 1 || memories[line_array[0]][0].Name == "SRAM0")
                        memories[line_array[0]][0].Name = "SRAM";
                }

                return memories;
            }

            List<Memory> FindMemories(string deviceName)
            {
                foreach (var deviceMask in _Memories.Keys)
                {
                    List<string> separated_device_masks = new List<string>();
                    if (deviceMask.Contains('|'))
                    {
                        string[] masks = deviceMask.Split('|');
                        separated_device_masks.Add(masks[0]);
                        for (int j = 1; j < masks.Length; j++)
                        {
                            separated_device_masks.Add(masks[0].Substring(0, masks[0].Length - masks[j].Length) + masks[j]);
                        }
                    }
                    else
                        separated_device_masks.Add(deviceMask);

                    foreach (var separated_device_mask in separated_device_masks)
                    {
                        if (separated_device_mask.Contains('x') || separated_device_mask.Contains('X'))
                        {
                            bool match = true;
                            // If the device name is shorter than the mask then assume the ? is not used for this device name
                            for (int j = 0; j < Math.Min(separated_device_mask.Length, deviceName.Length); j++)
                            {
                                if (separated_device_mask[j] == '?')
                                    continue;

                                if (separated_device_mask[j] != 'x' && separated_device_mask[j] != 'X'
                                    && separated_device_mask[j] != '?' && separated_device_mask[j] != deviceName[j])
                                {
                                    match = false;
                                    break;
                                }
                            }

                            if (match)
                                return _Memories[deviceMask];
                        }
                        else if (deviceName.StartsWith(separated_device_mask) || (separated_device_mask.StartsWith(deviceName) && deviceName.Length < separated_device_mask.Length))

                            return _Memories[deviceMask];
                    }
                }

                throw new Exception("Device " + deviceName + " memories not found!");
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                flashBase = FLASHBase;
                ramBase = SRAMBase;
            }

            public override MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                //No additional memory information available for this MCU. Build a basic memory layout from known RAM/FLASH sizes.
                MemoryLayout layout = new MemoryLayout { DeviceName = mcu.Name, Memories = FindMemories(mcu.Name) };

                layout.Memories.Insert(0, new Memory
                {
                    Name = "FLASH",
                    Access = MemoryAccess.Undefined,
                    Type = MemoryType.FLASH,
                    Start = FLASHBase,
                    Size = (uint)mcu.FlashSize
                });

                if (mcu.Name.StartsWith("LPC4", StringComparison.CurrentCultureIgnoreCase))
                {
                    int sram = mcu.RAMSize / 1024;
                    if ((sram == 24) || (sram == 40) || (sram == 80) || (sram == 96) || (sram == 104) || (sram == 136) || (sram == 168) || (sram == 200) || (sram == 264) || (sram == 282))
                    {
                        if ((layout.Memories[0].Size / 1024) == 0)
                            layout.Memories[0].Size = 64 * 1024;
                    }
                    else
                        throw new Exception("Unknown LPC43xx memory configuration");
                }

                if (mcu.FlashSize == 0)
                    layout.Memories[0].Size = 65536;

                return layout;
            }
        }

        enum enTypText {Text,MacroSection,MacroElseSection};       

        static string UpdateMacrosFile(string pFile,string pOutDir, string pBeginPars, string pEndPars,string pIgnorLine)
        {
            string aFileName ="";
            string [] aStrings = File.ReadAllLines(pFile);
            Regex regDefine = new Regex(@"^[ \t]*#define[ \t]+([\w]*)[ \t]+([\(\w\)]*]*)");//1 - macro //2 - Value
            Regex regDefineNoV = new Regex(@"^[ \t]*#define[ \t]+([\w]*).*");//1 - macro //2 - Value
            Regex regDef = new Regex(@"^[ \t]*#if defined[ \t\(]*([\w]*]*).*"); //1 - macro 
            Regex regDef1 = new Regex(@"^[ \t]*#elif defined[ \t\(]*([\w]*]*).*");//1 - macro
            Regex regDefEse = new Regex(@"^[ \t]*#else.*");//1 - macro
            Regex regDefEnd = new Regex(@"^[ \t]*#endif");

            Dictionary<string, string> aMacrosFile = new Dictionary<string, string>();
            enTypText aTypText = enTypText.Text;
            string aCurMacro = "";
            Dictionary<string,StringCollection > outFiles= new Dictionary<string, StringCollection>();


            aTypText = enTypText.Text;
            Directory.CreateDirectory(pOutDir);
            string aSourceFile = Path.GetFileNameWithoutExtension(pFile);
            outFiles.Add("ORIGINAL", new StringCollection());
            Boolean flPars = false;
            StringCollection aBegFile = new StringCollection();
           
            foreach (var aStr in aStrings)
            {          
                if (!flPars)
                    if (aStr.Contains(pBeginPars))
                        flPars = true;

                var m = regDefine.Match(aStr);
                if (m.Success)
                {
                    aMacrosFile.Add(m.Groups[1].Value, m.Groups[2].Value);
                    continue;
                }
                 m = regDefineNoV.Match(aStr);
                if (m.Success)
                {
                    aMacrosFile.Add(m.Groups[1].Value, m.Groups[1].Value);
                    continue;
                }
                

                if (flPars)
                {
                    if (aStr.Contains(pEndPars))
                       flPars = false;                     
                                        
                    if (aStr.Contains(pIgnorLine))
                        continue;

                    m = regDef.Match(aStr);

                    if (!m.Success)
                        m = regDef1.Match(aStr);

                    if (m.Success)
                    {
                        aTypText = enTypText.MacroSection;
                        aCurMacro = m.Groups[1].Value;
                        continue;
                    }
                    
                    m = regDefEse.Match(aStr);
                    if (m.Success)
                    {
                        aTypText = enTypText.MacroElseSection;
                        continue;
                    }
                    m = regDefEnd.Match(aStr);
                    if (m.Success)
                    {
                        aTypText = enTypText.Text;
                        continue;
                    }

                }

                if (aTypText == enTypText.MacroSection)
                {
                    StringCollection strc = null;
                    outFiles.TryGetValue(aCurMacro, out strc);
                    if (strc == null)
                    {
                        strc = new StringCollection();
                        foreach( var strl in aBegFile)
                            strc.Add(strl );
                        strc.Add(aStr);
                        outFiles.Add(aCurMacro, strc);
                    }
                    else
                        strc.Add(aStr);

                }

                if (aTypText == enTypText.Text)
                    foreach (var kv in outFiles)
                    {
                        kv.Value.Add(aStr);
                        aBegFile.Add(aStr);
                    }

                if (aTypText == enTypText.MacroElseSection)
                   foreach (var kv in outFiles)
                       if(kv.Key!=aCurMacro)
                            kv.Value.Add(aStr);
           }
       
        
            // out to header files 
            foreach (var kv in outFiles)
            {
                string aNewNameFile;
                if (aMacrosFile.ContainsKey(kv.Key))
                     aNewNameFile = pOutDir + "\\" +"DEF_"+ kv.Key + "_" + aSourceFile + ".c";
                else
                    aNewNameFile = pOutDir + "\\" + kv.Key + "_" + aSourceFile + ".c";

                using (var sw = File.CreateText(aNewNameFile))
                {
                   foreach(var strkv in  kv.Value)
                     sw.WriteLine(strkv);
                }

           }
                    return aFileName;
        }

        static bool GytTypMcuFile(string fnName,string pDevName)
        {
            string aTyp = "";
            fnName =  fnName.ToUpper();
            int idx = fnName.IndexOf("LPC");
            int idx2 = fnName.IndexOf("_",idx);
            if(idx2<0)
                idx2 = fnName.IndexOf(".", idx);

            if (idx2 < 0)
                idx2 = fnName.Length;


            aTyp = fnName.Substring(idx, idx2-idx);
            aTyp =  aTyp.Replace('X', '.');
            if (pDevName.Contains("LV"))
                if (!fnName.Contains("LV"))
                    return false;
            if (pDevName.Contains("11U6"))
                if (!fnName.Contains("11U6"))
                    return false;

            if (pDevName.Contains("176"))
                if (fnName.Contains("LPC175X_6"))
                    return true;
            if (pDevName.Contains("178"))
                if (fnName.Contains("LPC177X_8"))
                    return true;
            if (pDevName.Contains("LPC111"))
                if (fnName.Contains("LPC11C"))
                    return true;
            if (pDevName.Contains("LPC11D"))
                if (fnName.Contains("LPC11C"))
                    return true;

            if (pDevName.Contains("LPC1311") || pDevName.Contains("LPC1313") || pDevName.Contains("LPC1342"))
                if (fnName.Contains("LPC1343"))
                return true;

            if (pDevName.Contains("LPC1315") || pDevName.Contains("LPC1316") || pDevName.Contains("LPC1317") || pDevName.Contains("LPC1345") || pDevName.Contains("LPC1346"))
                if (fnName.Contains("LPC1347"))
                return true;

            aTyp += ".*";
            Regex rg = new Regex(aTyp);
            if(rg.IsMatch(pDevName))
                 return true;
            else
                return false;

        }

        static IEnumerable<StartupFileGenerator.InterruptVectorTable> ParseStartupFiles(string dir, string startupFileName, MCUFamilyBuilder fam)
        {
           string [] allFiles = Directory.GetFiles(dir);
           string aTemDir = fam.BSP.BSPRoot+"\\TempSource";
           foreach (var fn in allFiles)
            {
                string aFN = Path.GetFileNameWithoutExtension(fn);
                if (!aFN.StartsWith("cr_") || !aFN.EndsWith("x"))
                    continue;

                   UpdateMacrosFile(fn, aTemDir,"const g_pfnVectors[])(void) =", "};", "#error ");
            }

            allFiles = Directory.GetFiles(aTemDir);

            List<StartupFileGenerator.InterruptVector[]> list = new List<StartupFileGenerator.InterruptVector[]>();

            foreach (var fn in allFiles)
            {
                
                string subfamily = Path.GetFileNameWithoutExtension(fn);

                if (subfamily.StartsWith("CHIP_"))
                {
                    subfamily = subfamily.Substring(5, subfamily.IndexOf("_cr_start")-5);

                }else if (subfamily.StartsWith("ORIGINAL"))
                      {
                        int idx = subfamily.IndexOf("lpc");
                        subfamily = subfamily.Substring(idx, subfamily.Length - idx);
                      }
                       else
                              continue;

                list.Add(StartupFileGenerator.ParseInterruptVectors(fn,
                    @"void \(\* const g_pfnVectors\[\]\)\(void\) \=", 
                    @"[ \t]*\};",
                    @"([^ \t,]+)[,]?[ \t]+// ([^\(]+)",

                    @"([^ \t,]+)[,]?.*",
                    @"^[ \t]*//.*",

                    @"(USE_LPCOPEN_IRQHANDLER_NAMES)", 
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
                    else if (vectors[i].OptionalComment == "Reserved")
                    {
                        vectors[i] = null;
                        continue;
                    }else
                    {

                        for(int c = 0; c < i; c++)
                        {
                            if (vectors[c] != null)
                                if (vectors[c].Name == vectors[i].Name)
                                {
                                    int idx = vectors[c].OptionalComment.IndexOf(" ");
                                    if (idx == -1) idx = 0;

                                    vectors[i].Name = "INT_"+i+ "_" + vectors[i].Name;

                                }
                        }
                    }

                }
                yield return new StartupFileGenerator.InterruptVectorTable
                {
                    FileName = Path.ChangeExtension(Path.GetFileName(fn), ".c"),

                    MatchPredicate = m => (allFiles.Length == 1) || (GytTypMcuFile(Path.ChangeExtension(Path.GetFileName(fn), ".c"), m.Name)),

                     Vectors = vectors.ToArray()
                };
            }
            Directory.Delete(aTemDir, true);
        }

        private static IEnumerable<MCUDefinitionWithPredicate> ParsePeripheralRegisters(string dir, MCUFamilyBuilder fam)
        {
          
            List<MCUDefinitionWithPredicate> RegistersPeriphs = new List<MCUDefinitionWithPredicate>();

            Dictionary<string, HardwareRegisterSet[]> periphs = PeripheralRegisterGenerator.GenerateFamilyPeripheralRegisters(dir);
            
            foreach (var subfamily in periphs.Keys)
            {
                MCUDefinitionWithPredicate mcu_def = new MCUDefinitionWithPredicate { MCUName = subfamily, RegisterSets = periphs[subfamily], MatchPredicate = m => GytTypMcuFile(subfamily,m.Name), };
                RegistersPeriphs.Add(mcu_def);               
            }
                return RegistersPeriphs;
        }


        static List<MCUBuilder> RemoveDuplicateMCU(ref List<MCUBuilder> rawmcu_list)
        {
            //---------------------------------
            // Remove duplicate MCU
          //  List<MCUBuilder> rawmcu_list = pListMCU;
            rawmcu_list.Sort((a, b) => a.Name.CompareTo(b.Name));
            for (int ic = 0; ic < rawmcu_list.Count; ic++)
            {
                int DelCount = 0;
                MCUBuilder mcu = rawmcu_list[ic];
                Regex rg = new Regex(@"^LPC...[\d]*[L]?[V]?");
                string shortName = "";
                var m = rg.Match(mcu.Name);
                if (m.Success)
                    shortName = m.Groups[0].ToString();
                Boolean flModif = true;
                for (int il = ic + 1; il < rawmcu_list.Count; il++)
                {
                    MCUBuilder mcuNext = rawmcu_list[il];
                    m = rg.Match(mcuNext.Name);
                    string shortNameNext = "";
                    if (m.Success)
                        shortNameNext = m.Groups[0].ToString();

                    if (shortName != shortNameNext)
                        break;

                    if (mcu.FlashSize != mcuNext.FlashSize || mcu.RAMSize != mcuNext.RAMSize)
                        flModif = false;

                    DelCount++;
                }

                if (flModif)
                {
                    rawmcu_list[ic].Name = shortName;
                    rawmcu_list.RemoveRange(ic + 1, DelCount); //Delete duplicate
                }
                else
                    ic += DelCount;

            }

            return rawmcu_list;
          
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
                throw new Exception("Usage: nxp.exe <NXP SW package directory>");

            var bspBuilder = new NxpBSPBuilder(new BSPDirectories(args[0], @"..\..\Output", @"..\..\rules"));

            var devices = BSPGeneratorTools.ReadMCUDevicesFromCommaDelimitedCSVFile(bspBuilder.Directories.RulesDir + @"\McuNXPLpcDevices.csv",
                "Part Number", "Flash (KB)", "SRAM(kB)", "CPU", true);
            RemoveDuplicateMCU(ref devices);

            List<MCUFamilyBuilder> allFamilies = new List<MCUFamilyBuilder>();
            foreach (var fn in Directory.GetFiles(bspBuilder.Directories.RulesDir + @"\Families", "*.xml"))
                allFamilies.Add(new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(fn)));

            var rejects = BSPGeneratorTools.AssignMCUsToFamilies(devices, allFamilies);
            List<MCUFamily> familyDefinitions = new List<MCUFamily>();
            List<MCU> mcuDefinitions = new List<MCU>();
            List<EmbeddedFramework> frameworks = new List<EmbeddedFramework>();
            List<string> exampleDirs = new List<string>();
         
            
            bool noPeripheralRegisters = args.Contains("/noperiph");
            List<KeyValuePair<string, string>> macroToHeaderMap = new List<KeyValuePair<string, string>>();

            var commonPseudofamily = new MCUFamilyBuilder(bspBuilder, XmlTools.LoadObject<FamilyDefinition>(bspBuilder.Directories.RulesDir + @"\CommonFiles.xml"));
            var flags = new ToolFlags();
            List<string> projectFiles = new List<string>();
            commonPseudofamily.CopyFamilyFiles(ref flags, projectFiles);

            foreach (var sample in commonPseudofamily.CopySamples())
                exampleDirs.Add(sample);

            foreach (var fam in allFamilies)
            {
                var rejectedMCUs = fam.RemoveUnsupportedMCUs(true);
                if (rejectedMCUs.Length != 0)
                {
                    Console.WriteLine("Unsupported {0} MCUs:", fam.Definition.Name);
                    foreach (var mcu in rejectedMCUs)
                        Console.WriteLine("\t{0}", mcu.Name);
                }


                fam.AttachStartupFiles(ParseStartupFiles(fam.Definition.StartupFileDir, "cr_startup_lpc8xx.c", fam));
                if (!noPeripheralRegisters)
                    fam.AttachPeripheralRegisters(ParsePeripheralRegisters(fam.Definition.PrimaryHeaderDir +"\\" +fam.Definition.FamilySubdirectory,fam));

                var famObj = fam.GenerateFamilyObject(true);

                famObj.AdditionalSourceFiles = LoadedBSP.Combine(famObj.AdditionalSourceFiles, projectFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).ToArray());
                famObj.AdditionalHeaderFiles = LoadedBSP.Combine(famObj.AdditionalHeaderFiles, projectFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).ToArray());

                famObj.AdditionalSystemVars = LoadedBSP.Combine(famObj.AdditionalSystemVars, commonPseudofamily.Definition.AdditionalSystemVars);
                famObj.CompilationFlags = famObj.CompilationFlags.Merge(flags);
                famObj.CompilationFlags.PreprocessorMacros = LoadedBSP.Combine(famObj.CompilationFlags.PreprocessorMacros, new string[] { "$$com.sysprogs.bspoptions.primary_memory$$_layout" });

                familyDefinitions.Add(famObj);
                fam.GenerateLinkerScripts(false);
                foreach (var mcu in fam.MCUs)
                    mcuDefinitions.Add(mcu.GenerateDefinition(fam, bspBuilder, !noPeripheralRegisters));

                foreach (var fw in fam.GenerateFrameworkDefinitions())
                    frameworks.Add(fw);

                foreach (var sample in fam.CopySamples())
                    exampleDirs.Add(sample);
            }

            BoardSupportPackage bsp = new BoardSupportPackage
            {
                PackageID = "com.sysprogs.arm.nxp_lpc",
                PackageDescription = "NXP LPC Devices",
                GNUTargetID = "arm-eabi",
                GeneratedMakFileName = "nxp_lpc.mak",
                MCUFamilies = familyDefinitions.ToArray(),
                SupportedMCUs = mcuDefinitions.ToArray(),
                Frameworks = frameworks.ToArray(),
                Examples = exampleDirs.ToArray(),
                FileConditions = bspBuilder.MatchedFileConditions.ToArray(),
                PackageVersion = "2.1"
            };

            bspBuilder.Save(bsp, true);

        }
    }
}
