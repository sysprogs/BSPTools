using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static BSPGenerationTools.Parsing.RegularExpressionBuilder;

namespace stm32_bsp_generator
{
    class STM32ConfigFileParser : IConfigurationFileParser
    {
        static DefineClass MakeRegularDefineClass()
        {
            //The STM32 configuration macros can have several forms, e.g.:
            /*  #define HSE_VALUE    ((uint32_t)24000000)
             *  #define HSE_VALUE    24000000U
             *  #define HSE_VALUE    0x1234
             *  
             *  To account for all of them, we need a rather complicated regex (and a corresponding template), so we define
             *  each part of the regex separately and build both the regex and the template from it.
             */
            RegexComponent[] components = new[]
            {
                 RC(" *", ""),                                              //Initial padding
                 RC("#define", RegexComponentKind.Fixed),                   //#define
                 RC("[ \t]+", " "),                                         //Space between #define and macro
                 RC("[^ \t]+", RegexComponentKind.Name),                    //Macro name
                 RC("[ \t]+", " "),                                         //Space between name and value
                 RC(@"\(?"),                                                //Possible start of type conversion     (
                 RC(@"|\([a-zA-Z0-9_]+\)"),                                 //Possible type conversion              (uint32_t)
                 RC("0x[0-9a-fA-F]+|[0-9]+", RegexComponentKind.Value),     //Value                                 0x1234
                 RC("U|u|"),                                                //Possible 'U' suffix                   U
                 RC(@"\)?"),                                                //Possible end of type conversion       )
                 RC(@"| */\*!<.*\*/", RegexComponentKind.Comment),          //Possible comment                      /*<! xxx */
            };

            return new DefineClass(components);
        }

        public ConfigurationFileTemplateEx BuildConfigurationFileTemplate(string file, ConfigFileDefinition cf)
        {
            Regex rgIfndef = new Regex("^#ifndef ([^ ]+)");

            DefineClass valuelessDefine = new DefineClass(@"#define ([^ ]+)( *)$",
                "#define {name}{g2: }",
                @"^/\* *#define ([^ ]+)( *)\*/$",
                "/* #define {name} */");


            DefineClass defineWithValue = MakeRegularDefineClass();

            Regex rgGroup = new Regex(@" */\* #+ ([^#]*) #+ \*/");
            Regex rgHalModuleMacro = new Regex("HAL_(.*)MODULE_ENABLED");

            PropertyList propertyList = new PropertyList { PropertyGroups = new List<PropertyGroup>() };
            PropertyGroup group = null;
            string lastIfndef = null;
            List<TestableConfigurationFileParameter> testableParameters = new List<TestableConfigurationFileParameter>();

            foreach (var line in File.ReadAllLines(file))
            {
                string previousLineIfndef = lastIfndef;
                lastIfndef = null;
                Match m;
                bool isInverse;

                if (line.Trim() == "")
                    continue;

                if ((m = rgGroup.Match(line)).Success)
                {
                    if (group != null && group.Properties.Count > 0)
                        propertyList.PropertyGroups.Add(group);

                    group = new PropertyGroup { Name = m.Groups[1].Value.Trim() };
                }
                else if ((m = rgIfndef.Match(line)).Success)
                    lastIfndef = m.Groups[1].Value;
                else
                {
                    PropertyEntry prop = null;

                    if (valuelessDefine.IsMatch(line, out m, out isInverse))
                    {
                        var macro = m.Groups[valuelessDefine.MacroNameGroup].Value;
                        if (macro.EndsWith("HAL_CONF_H"))
                            continue;

                        valuelessDefine.FoundDefines.Add(macro);
                        string userFriendlyName = macro;

                        if ((m = rgHalModuleMacro.Match(macro)).Success)
                        {
                            string moduleName = m.Groups[1].Value;
                            if (moduleName == "")
                                userFriendlyName = "Enable the HAL framework";
                            else
                                userFriendlyName = $"Enable the {moduleName.TrimEnd('_')} module";
                        }

                        prop = new PropertyEntry.Boolean { Name = userFriendlyName, ValueForTrue = "1", ValueForFalse = "", UniqueID = macro, DefaultValue = !isInverse };

                        if (macro != "HAL_MODULE_ENABLED")
                            testableParameters.Add(new TestableConfigurationFileParameter { Name = macro, DisabledValue = "", EnabledValue = "1" });
                    }
                    else if (defineWithValue.IsMatch(line, out m, out isInverse))
                    {
                        var macro = m.Groups[defineWithValue.MacroNameGroup].Value;
                        var value = m.Groups[defineWithValue.ValueGroup].Value;
                        var text = m.Groups[defineWithValue.CommentGroup].Value.Trim('*', '/', '!', '<', ' ');
                        if (text == "")
                            text = null;
                        else
                            text = $"{text} ({macro})";

                        defineWithValue.FoundDefines.Add(macro);

                        if ((macro.StartsWith("USE_") || macro.EndsWith("_ENABLED")) && (value == "0" || value == "1" || value == "0x1"))
                        {
                            prop = new PropertyEntry.Boolean { Name = text ?? macro, UniqueID = macro, ValueForTrue = "1", ValueForFalse = "0", DefaultValue = value != "0" };
                        }
                        else if (int.TryParse(value, out var intValue) || (value.StartsWith("0x") && int.TryParse(value.Substring(2), NumberStyles.HexNumber, null, out intValue)))
                            prop = new PropertyEntry.Integral { Name = text ?? macro, UniqueID = macro, DefaultValue = intValue };
                        else
                            prop = new PropertyEntry.String { Name = text ?? macro, UniqueID = macro, DefaultValue = value };
                    }

                    if (prop != null)
                    {
                        if (group == null)
                            throw new Exception("Property group could not be parsed. Please double-check " + file);

                        group.Properties.Add(prop);
                    }
                }
            }


            var template = new ConfigurationFileTemplate
            {
                PropertyClasses = new[] { valuelessDefine, defineWithValue }.Select(d => d.ToPropertyClass()).ToArray(),
                TargetFileName = Path.GetFileName(file),
                PropertyList = propertyList,
                UserFriendlyName = "STM32 HAL Configuration",
            };

            return new ConfigurationFileTemplateEx(template)
            {
                TestableHeaderFiles = cf.TestableHeaderFiles,
                TestableParameters = testableParameters.ToArray(),
            };
        }
    }
}
