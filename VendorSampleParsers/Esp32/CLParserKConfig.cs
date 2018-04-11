using System;
using BSPEngine;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace Esp32VendorSampleParser
{
    class CLParserKConfig
    {

        const string NameFileMacrInnerValue = "sdkonfigmacros.h";
        static string outputDir = @"..\..\Output";
        static List<PropertyEntry> LisrtAllPr = new List<PropertyEntry>();
        static List<string> lstShortMacr = new List<string>();
        const string strPrefixConfProperty = "";// CONFIG_";
        static int countNoNameProp = 0;
        // parser type config 
        static PropertyEntry ParserProperty(string[] pKFile, ref int countline)
        {
            string ln, TypeProperty = "none", Name = "", lstHelp = "", Macros = "", DefValue = "";
            uint max = UInt32.MaxValue, resParseU;
            PropertyEntry PropEntry = null;
            int resParse, min = -1;
            bool flHelp = false;
            bool typNoName = false;

            while (countline < pKFile.Count())
            {
                ln = pKFile[countline];
                if (ln.StartsWith("#"))
                { countline++; continue; }


                if (!ln.StartsWith("config") && !ln.StartsWith("menuconfig") && Macros == "")
                    return null;

                if (ln.StartsWith("end") || ln.StartsWith("source") || ln.StartsWith("menu "))
                    if (Macros != "")//заполнены поля 
                    { countline--; break; }
                    else return null;


                Match m = Regex.Match(ln, "^(menuconfig|config|choice)[ ]+([A-Z0-9_]+)");
                if (m.Success)
                {
                    if (TypeProperty != "none")//parsing is done
                    { countline--; break; }

                    if (m.Groups[1].Value != "config" && m.Groups[1].Value != "menuconfig")
                    { countline--; return null; }

                    Macros = strPrefixConfProperty + m.Groups[2].Value;
                }
                if (flHelp)
                    lstHelp += ln;
                else
                {

                    m = Regex.Match(ln, "^[ \t]+(int|bool|hex|string)[ ]*[\"]?(['\\w0-9_ \\(\\)-]*)[\"]?");
                    if (m.Success)
                    {
                        TypeProperty = m.Groups[1].Value;
                        if (m.Groups[2].Value == "")
                        {
                            countNoNameProp++;
                            Console.WriteLine(ln);
                            if (false)
                            { Name = Macros; typNoName = true; }
                            else
                            {
                                if (Macros == "ESP32_PHY_MAX_TX_POWER" || Macros == "PHY_ENABLED")
                                    Name = Macros;

                                else { TypeProperty = "none"; countline++; continue; }// no dublecate 
                            }
                        }
                        else
                            Name = m.Groups[2].Value;
                        //TypeProperty = m.Groups[1].Value;
                    }
                    else
                    {
                        if (!typNoName)
                        {
                            m = Regex.Match(ln, "^[ \t]+default[\t ]+(['/\"_\\w 0-9-]*)");
                            if (m.Success)
                                DefValue = m.Groups[1].Value;
                        }
                        else
                        {
                            if (ln.Contains("default"))
                            {
                                //    flHelp = true;
                                Console.WriteLine(ln);
                            }
                        }
                        m = Regex.Match(ln, "^[ \t]+range[ \t]([\\w\\d]+)[ \t]+([\\w\\d]+)");

                        if (m.Success)
                        {
                            var minInt = Int32.TryParse(m.Groups[1].Value, out resParse) ? Int32.Parse(m.Groups[1].Value) : 0;
                            var maxInt = UInt32.TryParse(m.Groups[2].Value, out resParseU) ? UInt32.Parse(m.Groups[2].Value) : UInt32.MaxValue;
                            min = ((min < minInt) && (min > -1)) ? min : minInt;
                            max = maxInt;
                        }

                        if (Regex.IsMatch(ln, "^[ \t]+help[ \t]*"))
                            flHelp = true;
                    }
                }
                countline++;
            }
            // save
            switch (TypeProperty)
            {
                case "string":
                    PropEntry = new PropertyEntry.String()
                    {
                        Name = Name,
                        UniqueID = Macros,
                        Description = lstHelp,
                        DefaultValue = DefValue
                    };
                    break;
                case "bool":
                    PropEntry = new PropertyEntry.Boolean()
                    {
                        Name = Name,
                        UniqueID = Macros,
                        Description = lstHelp,
                        DefaultValue = DefValue.ToLower().Contains("y") ? true : false
                    };
                    break;
                case "int":
                case "hex":
                    PropEntry = new PropertyEntry.Integral()
                    {
                        Name = Name,
                        UniqueID = Macros,
                        Description = lstHelp,
                        DefaultValue = Int32.TryParse(DefValue, out resParse) ? Int32.Parse(DefValue) : 0,
                        MinValue = (min == -1) ? 0 : min,
                        MaxValue = (min == -1) ? Int32.MaxValue : (int)max
                    };
                    break;
                case "none":
                    return null;
                default:
                    throw new Exception(" unknow type config");
            }
            return PropEntry;
        }
        //-------------------------------------------------------
        static string ChangeInternelValue(ref List<PropertyEntry.Enumerated.Suggestion> pParamEnum)
        {
            int cl, otlett = 0;
            var pr1 = pParamEnum[0].InternalValue;
        
            for (cl = 0; cl < pr1.Length && otlett == 0; cl++)
                foreach (var pr in pParamEnum)
                    if (pr.InternalValue[cl] != pr1[cl])
                    {
                        otlett = cl;
                        break;
                    }


            foreach (var pr in pParamEnum)
                pr.InternalValue = pr.InternalValue.Remove(0, otlett);

            return pr1.Remove(otlett);
        }
        // perset Choice type
        static PropertyEntry ParserChoice(string[] pKFile, ref int countline)
        {
            string ln, NamePromptr = "", DefValueChoice = "", lsHelp = "", EnumUID = "";
            List<PropertyEntry> ListProperties = new List<PropertyEntry>();
            List<PropertyEntry.Enumerated.Suggestion> lstSugEnum = new List<PropertyEntry.Enumerated.Suggestion>();
            while (countline < pKFile.Count())
            {
                ln = pKFile[countline];
                if (ln.StartsWith("#"))
                { countline++; continue; }

                Match m = Regex.Match(ln, "^(choice)[ ]+([A-Z0-9_]+)");
                if (m.Success)
                    EnumUID = m.Groups[2].Value;
                else if (EnumUID == "")
                    return null;

                if (ln.StartsWith("endchoice"))
                    break;

                m = Regex.Match(ln, "^[ \t]+(prompt|bool)[ ]*[\"]?(['\\w0-9_ ]*)[\"]?");
                if (m.Success)
                    if (m.Groups[2].Value != "")
                        NamePromptr = m.Groups[2].Value;
                    else
                    {
                        countNoNameProp++;
                        Console.WriteLine(ln);
                    }

                m = Regex.Match(ln, "^[ \t]+default[ \t]([\\w\\d]+)");
                if (m.Success)
                    DefValueChoice = m.Groups[1].Value;


                if (Regex.IsMatch(ln, "^[ \t]+help[ \t]*"))
                {
                    while (countline < pKFile.Count())
                    {
                        countline++;
                        ln = pKFile[countline];
                        m = Regex.Match(ln, "^(config|choice)[ ]+([A-Z0-9_]+)");
                        if (m.Success)
                            break;
                        if (!ln.StartsWith("#"))
                            lsHelp += ln;
                    }
                }

                var prop = ParserProperty(pKFile, ref countline);

                if (prop != null)
                {   
                    //----------------------
                    if (prop.UniqueID == "TWO_UNIVERSAL_MAC_ADDRESS")
                        prop.UniqueID = "2//UNIVERSAL_MAC_ADDRESS";

                    if (prop.UniqueID == "FOUR_UNIVERSAL_MAC_ADDRESS")
                        prop.UniqueID = "4//UNIVERSAL_MAC_ADDRESS";


                    //----------------------
                    lstSugEnum.Add(new PropertyEntry.Enumerated.Suggestion()
                    {
                        UserFriendlyName = prop.Name,
                        InternalValue = prop.UniqueID
                    });
                }
                countline++;
            }
            int defIndex = lstSugEnum.FindIndex(pr => pr.InternalValue.Remove(0, strPrefixConfProperty.Length) == DefValueChoice);
            if (!lstSugEnum[0].InternalValue.Contains("//UNIVERSAL_MAC_ADDRESS"))
                lstShortMacr.Add($"#define    CONFIG_{ChangeInternelValue(ref lstSugEnum)}$${EnumUID}$$    1");
            else
                defIndex = 1;

            PropertyEntry.Enumerated Enum = new PropertyEntry.Enumerated()
            {
                Name = NamePromptr,
                Description = lsHelp,
                UniqueID = EnumUID,
                SuggestionList = lstSugEnum.ToArray(),
                DefaultEntryIndex = defIndex
            };
            return Enum;
        }

        //-------------------------------------------------------
        //parsing KConfig
        static List<PropertyEntry> ParserFileKConfig(string pKFile)
        {
            List<PropertyEntry> ListProperties = new List<PropertyEntry>();
            PropertyEntry PropEntry = new PropertyEntry.Boolean();

            if (pKFile.EndsWith("txt"))
                return null;

            var strFile = File.ReadAllLines(pKFile);

            if (strFile.ToList().Where(s => s.Contains("menu \"Example Configuration\"")).Count() != 0)
                return null;

            // Delete comment #
            for (int countln = 0; countln < strFile.Count(); countln++)
                if (strFile[countln].IndexOf('#') > 0)
                    strFile[countln] = strFile[countln].Remove(strFile[countln].IndexOf('#'));

            // Parser KConfig
            for (int countln = 0; countln < strFile.Count(); countln++)
            {
                var prop = ParserChoice(strFile, ref countln);
                if (prop != null)
                    ListProperties.Add(prop);
                else
                {
                    prop = ParserProperty(strFile, ref countln);
                    if (prop != null)
                        ListProperties.Add(prop);
                }
            }
            /// save
            return ListProperties;
        }
        static public void ParserAllFilesKConfig(string pKFileDir)
        {
            int CountFile = 0;
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);
            LisrtAllPr.Clear();
            foreach (var kc in Directory.GetFiles(pKFileDir, "KConfig*", SearchOption.AllDirectories))
            {
                File.Copy(kc, $@"{outputDir}\KConfig{++CountFile}", true);
                Console.WriteLine($"{CountFile} - {kc}");
                var ListProperties = ParserFileKConfig(kc);
                List<PropertyGroup> lstPrGr = new List<PropertyGroup>();

                if (ListProperties != null)
                    lstPrGr.Add(new PropertyGroup() { Properties = ListProperties });

                XmlTools.SaveObject(new PropertyList { PropertyGroups = lstPrGr }, $@"{outputDir}\Sdkonf{CountFile}.xml");
                if (ListProperties != null)
                    LisrtAllPr.AddRange(ListProperties);
            }

            Console.WriteLine($"No name {countNoNameProp}");

            List<PropertyGroup> lstPrGrAll = new List<PropertyGroup>();
            lstPrGrAll.Add(new PropertyGroup() { Properties = LisrtAllPr });
            XmlTools.SaveObject(new PropertyList { PropertyGroups = lstPrGrAll }, $@"{outputDir}\sdkonfig.xml");

            if (lstShortMacr.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).Count() > 0)
                throw new Exception(" dublicate macros ");

            File.WriteAllLines($@"{outputDir}\{NameFileMacrInnerValue}", lstShortMacr.ToArray());
        }
        //-----------------------------------------------
        static public void SdkconfigChangeMacros(string sdkfile)
        {
            string[] strFile = File.ReadAllLines(sdkfile);
            File.WriteAllLines(sdkfile + "old", strFile);
            for (int c = 0; c < strFile.Count(); c++)
            {
                Match m = Regex.Match(strFile[c], "#define[ \t]+(CONFIG_([\\w\\d_]*))");
                if (!m.Success)
                    continue;
                strFile[c] = $"#define  {m.Groups[1].Value}$${ m.Groups[2].Value}$$    ";
            }
            File.WriteAllLines(sdkfile, strFile);
        }
        //-----------------------------------------------
        static public void GenerateSdkconfigFile()
        {
            using (var sw = new StreamWriter(Path.Combine(outputDir, "sdkconfig.h")))
            {
                sw.WriteLine("/*Automatically generated file; DO NOT EDIT.");
                sw.WriteLine("* Espressif IoT Development Framework Configuration*/");
                sw.WriteLine($" ");
                sw.Write(File.ReadAllText($@"{outputDir}\{NameFileMacrInnerValue}"));// WriteLine(ln);
                sw.WriteLine($" ");


                if (LisrtAllPr.GroupBy(v => v.UniqueID).Where(g => g.Count() > 1).Select(g => g.Key).Count() > 0)
                    throw new Exception(" dublicate macros ");

                foreach (var mc in LisrtAllPr)
                {
                    var e = mc.GetType().Name;
                    if (e == "fefer")
                        return;
                }
                foreach (var mc in LisrtAllPr)
                {
                    string macros = mc.UniqueID;
                    string macros_param = macros;
                    string prefix_macros = "";
                    if (macros == "CONSOLE_UART" || macros == "CONSOLE_UART_NUM")
                        macros_param = "CONSOLE_UART_CUSTOM_NUM";
                    if (macros == "ESP32_DEFAULT_CPU_FREQ_MHZ")
                        macros_param = "ESP32_DEFAULT_CPU_FREQ";
 
                    if (macros != "NUMBER_OF_UNIVERSAL_MAC_ADDRESS")
                        prefix_macros = $"CONFIG_{macros_param}_";

                    if (macros == "BROWNOUT_DET_LVL_SEL")
                        macros = $"CONFIG_BROWNOUT_DET_LVL";

                    if (mc.GetType().Name == "Enumerated")                   
                        sw.WriteLine($"#define CONFIG_{macros}    {prefix_macros}$${macros}$$");
                    else
                        sw.WriteLine($"#define CONFIG_{macros}    $${macros}$$");
                }

            }
        }
    }
}
