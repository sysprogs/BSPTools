using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace stm32_bsp_generator
{
    class STM32ConfigFileParser : IConfigurationFileParser
    {
        class DefineClass
        {
            public Regex Define;
            public Regex CommentedDefine;
            public List<string> FoundDefines = new List<string>();

            public string Template, InverseTemplate;

            public int MacroNameGroup = 1, ValueGroup = 3, CommentGroup = 4;

            public DefineClass(RegexComponent[] components)
            {
                for (int i = 0; i < components.Length; i++)
                    components[i].Index = i + 1;

                Define = new Regex(string.Join("", components.Select(c => c.ToRegexPart())) + "$");
                Template = string.Join("", components.Select(c => c.ToTemplatePart()));

                MacroNameGroup = components.Single(c => c.Kind == RegexComponentKind.Name).Index;
                ValueGroup = components.Single(c => c.Kind == RegexComponentKind.Value).Index;
                CommentGroup = components.Single(c => c.Kind == RegexComponentKind.Comment).Index;
            }

            public DefineClass(string regex, string template, string inverseRegex = null, string inverseTemplate = null, bool hasPrePadding = false)
            {
                Define = new Regex(regex);
                Template = template;
                if (inverseRegex != null)
                {
                    CommentedDefine = new Regex(inverseRegex);
                    InverseTemplate = inverseTemplate;
                }

                if (hasPrePadding)
                {
                    MacroNameGroup++;
                    ValueGroup++;
                }
            }

            public bool IsMatch(string line, out Match m, out bool isInverse)
            {
                m = Define.Match(line);
                if (m.Success)
                {
                    isInverse = false;
                    return true;
                }

                if (CommentedDefine != null)
                {
                    m = CommentedDefine.Match(line);
                    if (m.Success)
                    {
                        isInverse = true;
                        return true;
                    }
                }

                isInverse = false;
                return false;
            }

            public ConfigurationFilePropertyClass ToPropertyClass()
            {
                return new ConfigurationFilePropertyClass
                {
                    NormalRegex = new SerializableRegularExpression(Define.ToString()),
                    UndefRegex = CommentedDefine == null ? null : new SerializableRegularExpression(CommentedDefine.ToString()),
                    Template = Template,
                    UndefTemplate = InverseTemplate,
                    Properties = FoundDefines.ToArray(),
                    NameIndex = MacroNameGroup,
                    ValueIndex = ValueGroup,
                };
            }
        }

        enum RegexComponentKind
        {
            Padding,
            Fixed,
            Name,
            Value,
            Comment,
        }

        struct RegexComponent
        {
            public string Regex;
            public string DefaultValue;
            public RegexComponentKind Kind;

            public int Index;

            public string ToRegexPart() => "(" + Regex + ")";
            public string ToTemplatePart()
            {
                switch (Kind)
                {
                    case RegexComponentKind.Padding:
                    case RegexComponentKind.Comment:
                        if (string.IsNullOrEmpty(DefaultValue))
                            return "{g" + Index + "}";
                        else
                            return "{g" + Index + ":" + DefaultValue + "}";
                    case RegexComponentKind.Fixed:
                        return DefaultValue ?? Regex;
                    case RegexComponentKind.Name:
                        return "{name}";
                    case RegexComponentKind.Value:
                        return "{value}";
                    default:
                        throw new Exception("Unknown regex part");
                }
            }
        }

        static RegexComponent RC(string regex, string defaultValue, RegexComponentKind kind = RegexComponentKind.Padding) => new RegexComponent { Regex = regex, DefaultValue = defaultValue, Kind = kind };
        static RegexComponent RC(string regex, RegexComponentKind kind = RegexComponentKind.Padding) => new RegexComponent { Regex = regex, Kind = kind };

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
                 RC(" *", " "),                                             //Space between #define and macro
                 RC("[^ ]+", RegexComponentKind.Name),                      //Macro name
                 RC(" *"),                                                  //Space between name and value
                 RC(@"\(?"),                                                //Possible start of type conversion
                 RC(@"|\([a-zA-Z0-9_]+\)"),                                 //Possible type conversion
                 RC("0x[0-9a-fA-F]+|[0-9]+", RegexComponentKind.Value),     //Value
                 RC("U|u|"),                                                //Possible 'U' suffix
                 RC(@"\)?"),                                                //Possible end of type conversion
                 RC(@"| */\*!<.*\*/", RegexComponentKind.Comment),          //Possible comment
            };

            return new DefineClass(components);
        }

        public ConfigurationFileTemplate BuildConfigurationFileTemplate(string file)
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
                            prop = new PropertyEntry.Boolean { Name = text ?? macro, UniqueID = macro, ValueForTrue = "1", ValueForFalse = "", DefaultValue = value != "0" };
                        else
                            prop = new PropertyEntry.Integral { Name = text ?? macro, UniqueID = macro, DefaultValue = int.Parse(value) };
                    }

                    if (prop != null)
                    {
                        if (group == null)
                            throw new Exception("Property group could not be parsed. Please double-check " + file);

                        group.Properties.Add(prop);
                    }
                }
            }


            return new ConfigurationFileTemplate
            {
                PropertyClasses = new[] { valuelessDefine, defineWithValue }.Select(d => d.ToPropertyClass()).ToArray(),
                TargetFileName = Path.GetFileName(file),
                PropertyList = propertyList,
                UserFriendlyName = "STM32 HAL Configuration",
            };
        }
    }
}
