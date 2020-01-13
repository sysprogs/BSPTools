using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BSPGenerationTools.Parsing
{
    public class RegularExpressionBuilder
    {
        public enum RegexComponentKind
        {
            Padding,
            Fixed,
            Name,
            Value,
            Comment,
        }

        public struct RegexComponent
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

        public static RegexComponent RC(string regex, string defaultValue, RegexComponentKind kind = RegexComponentKind.Padding) => new RegexComponent { Regex = regex, DefaultValue = defaultValue, Kind = kind };
        public static RegexComponent RC(string regex, RegexComponentKind kind = RegexComponentKind.Padding) => new RegexComponent { Regex = regex, Kind = kind };

        public class DefineClass
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
    }
}
