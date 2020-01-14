using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BSPEngine;
using static BSPGenerationTools.Parsing.RegularExpressionBuilder;

namespace BSPGenerationTools.ConfigFiles
{
    class FreeRTOSConfigParser : IConfigurationFileParser
    {
        static DefineClass MakeRegularDefineClass()
        {
            //Based on STM32 define class. See STM32ConfigFileParser.cs for explanation.
            RegexComponent[] components = new[]
            {
                 RC(" *", ""),                                              //Initial padding
                 RC("#define", RegexComponentKind.Fixed),                   //#define
                 RC("[ \t]+", " "),                                         //Space between #define and macro
                 RC("[^ \t]+", RegexComponentKind.Name),                    //Macro name
                 RC("[ \t]+", " "),                                         //Space between name and value
                 RC(@"\(?"),                                                //Possible start of type conversion     (
                 RC(@" *\( *[a-zA-Z0-9_]+ *\) *|"),                         //Possible type conversion              (uint32_t)
                 RC(@"[( ]*"),                                              //Possible opening bracket around value (
                 RC("[a-zA-Z0-9_]+|[a-zA-Z0-9_][^()]+[a-zA-Z0-9_]", RegexComponentKind.Value),                    //Value
                 RC(@"[ )]*"),                                              //Possible closing bracket around value )
                 RC(@"\)?"),                                                //Possible end of type conversion       )
                 RC(@"| */\*.*\*/", RegexComponentKind.Comment),            //Possible comment                      /* xxx */
            };

            return new DefineClass(components);
        }

        public ConfigurationFileTemplateEx BuildConfigurationFileTemplate(string file, ConfigFileDefinition cf)
        {
            PropertyGroup group = new PropertyGroup { Name = "FreeRTOS" };
            PropertyList propertyList = new PropertyList { PropertyGroups = new List<PropertyGroup> { group } };

            var defClass = MakeRegularDefineClass();
            const string namePrefix = "config";
            HashSet<string> processedNames = new HashSet<string>();

            foreach (var line in File.ReadAllLines(file))
            {
                if (defClass.IsMatch(line, out var m, out var unused) && m.Groups[defClass.MacroNameGroup].Value.StartsWith(namePrefix))
                {
                    string name = m.Groups[defClass.MacroNameGroup].Value;
                    if (processedNames.Contains(name))
                        continue;

                    if (name == "ASSERT")
                        continue;   //configASSERT is a function-like macro, not a regular constant-like definition.

                    processedNames.Add(name);

                    if (name.StartsWith("configUSE_"))
                        group.Properties.Add(new PropertyEntry.Boolean { Name = name.Substring(namePrefix.Length), UniqueID = name, ValueForTrue = "1", ValueForFalse = "" });
                    else
                        group.Properties.Add(new PropertyEntry.String { Name = name.Substring(namePrefix.Length), UniqueID = name });

                    defClass.FoundDefines.Add(name);
                }
            }

            var template = new ConfigurationFileTemplate
            {
                PropertyClasses = new ConfigurationFilePropertyClass[]
                {
                    defClass.ToPropertyClass()
                },

                TargetFileName = Path.GetFileName(file),
                PropertyList = propertyList,
                UserFriendlyName = "FreeRTOS Configuration",
            };

            return new ConfigurationFileTemplateEx(template);
        }
    }
}
