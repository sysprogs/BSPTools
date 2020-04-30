using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;

namespace KSDK2xImporter.HelperTypes
{
    enum ComponentType
    {
        Skipped,
        LinkerScript,
        SVDFile,
        CMSIS_SDK,
        Other,
    }

    class ParsedComponent
    {
        public readonly ComponentType Type;
        public readonly ParsedFilter Filter;

        public readonly ParsedSourceList[] SourceLists;

        public readonly string ID, Name, OriginalType;
        public readonly string[] Dependencies;

        public bool IsSourceComponent => Type == ComponentType.CMSIS_SDK || Type == ComponentType.Other;
        public bool SkipUnconditionally => Type == ComponentType.Skipped || Filter.SkipUnconditionally;

        public override string ToString() => $"{Name} ({OriginalType})";

        ComponentType TranslateComponentType(string type) => type switch
        {
            "documentation" => ComponentType.Skipped,
            "SCR" => ComponentType.Skipped,
            "EULA" => ComponentType.Skipped,
            "project_template" => ComponentType.Skipped,
            "debugger" => ComponentType.SVDFile,
            "linker" => ComponentType.LinkerScript,
            "CMSIS" => ComponentType.CMSIS_SDK,
            _ => ComponentType.Other,
        };

        public IEnumerable<FileReference> LocateAllFiles(SpecializedDevice device, string rootDir)
        {
            return SourceLists.Where(sl => sl.Filter.MatchesDevice(device)).SelectMany(s => s.LocateAllFiles(device, rootDir));
        }

        public ParsedComponent(XmlElement componentNode)
        {
            ID = componentNode.GetAttribute("id");
            Name = componentNode.GetAttribute("name");
            OriginalType = componentNode.GetAttribute("type");
            Type = TranslateComponentType(OriginalType);

            Filter = new ParsedFilter(componentNode);

            SourceLists = componentNode.SelectNodes("source").OfType<XmlElement>().Select(e => new ParsedSourceList(e)).ToArray();

            var dependencies = componentNode.GetAttribute("dependencies");
            if (dependencies != "")
                Dependencies = dependencies.Split(' ');
            else
                Dependencies = componentNode.SelectNodes("dependencies/all/component_dependency/@value").OfType<XmlAttribute>().Select(a => a.Value).ToArray();


        }
    }
}
