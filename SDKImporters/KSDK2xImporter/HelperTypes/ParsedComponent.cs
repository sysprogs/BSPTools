using BSPEngine;
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
        public const string FrameworkIDPrefix = "com.sysprogs.ksdk2x_imported.";

        public readonly ComponentType Type;
        public readonly ParsedFilter Filter;

        public readonly ParsedSourceList[] SourceLists;
        public readonly ParsedDefine[] Defines;
        public readonly string ID, Name, OriginalType, LongName;
        public readonly string[] Dependencies;

        public bool IsSourceComponent => Type == ComponentType.CMSIS_SDK || Type == ComponentType.Other;
        public bool SkipUnconditionally => Type == ComponentType.Skipped || Filter.SkipUnconditionally;

        public override string ToString() => $"{Name} ({OriginalType})";

        public bool ReferenceImplicitly => Name == "Include" || ID == "platform.CMSIS";

        public string TargetFrameworkID => FrameworkIDPrefix + ID;

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

            LongName = componentNode.GetAttribute("full_name");
            if (string.IsNullOrEmpty(LongName))
                componentNode.GetAttribute("brief");

            if (string.IsNullOrEmpty(LongName))
                LongName = Name;

            Filter = new ParsedFilter(componentNode);
            SourceLists = componentNode.SelectNodes("source").OfType<XmlElement>().Select(e => new ParsedSourceList(e)).ToArray();
            Defines = componentNode.SelectNodes("defines/define").OfType<XmlElement>().Select(el => new ParsedDefine(el)).ToArray();

            var dependencies = componentNode.GetAttribute("dependencies");
            if (dependencies != "")
                Dependencies = dependencies.Split(' ');
            else
                Dependencies = componentNode.SelectNodes("dependencies/all/component_dependency/@value").OfType<XmlAttribute>().Select(a => a.Value).ToArray();
        }

        public EmbeddedFramework BuildFramework(string rootDir,
            IWarningSink sink,
            Dictionary<string, FileCondition> fileConditions,
            string projectFolderName,
            HashSet<string> allComponentIDs,
            string mcuRegex = null,
            SpecializedDevice specificDevice = null)
        {
            var headerFiles = new HashSet<string>();
            var includeDirectories = new HashSet<string>();
            var sourceFiles = new HashSet<string>();
            var libFiles = new HashSet<string>();

            foreach (var src in SourceLists)
            {
                if (!src.Filter.MatchesDevice(specificDevice))
                    continue;

                foreach (var file in src.LocateAllFiles(specificDevice, rootDir))
                {
                    string bspPath = file.GetBSPPath();
                    file.UpdateIncludeDirectoryList(includeDirectories);

                    switch (file.Type)
                    {
                        case SourceType.Header:
                            headerFiles.Add(bspPath);
                            break;
                        case SourceType.Source:
                            sourceFiles.Add(bspPath);
                            break;
                        case SourceType.Library:
                            libFiles.Add(bspPath);
                            break;
                    }

                    if (!string.IsNullOrEmpty(src.ExtraCondition))
                    {
                        if (allComponentIDs.Contains(src.ExtraCondition))
                        {
                            string frameworkID = FrameworkIDPrefix + src.ExtraCondition;

                            if (fileConditions.TryGetValue(bspPath, out var oldCondition) && (oldCondition.ConditionToInclude as Condition.ReferencesFramework)?.FrameworkID != frameworkID)
                                sink.LogWarning("Duplicate conditions for " + bspPath);

                            fileConditions[bspPath] = new FileCondition { FilePath = bspPath, ConditionToInclude = new Condition.ReferencesFramework { FrameworkID = frameworkID } };
                        }
                        else
                        {
                            sink.LogWarning("Unrecognized condition: " + src.ExtraCondition);
                        }
                    }
                }
            }

            var framework = new EmbeddedFramework
            {
                ID = TargetFrameworkID,
                MCUFilterRegex = mcuRegex,
                UserFriendlyName = $"{LongName} ({OriginalType})",
                ProjectFolderName = projectFolderName,
                RequiredFrameworks = Dependencies.Where(d => allComponentIDs.Contains(d)).Select(d => FrameworkIDPrefix + d).ToArray(),
                AdditionalSourceFiles = sourceFiles.ToArray(),
                AdditionalHeaderFiles = headerFiles.ToArray(),
                AdditionalIncludeDirs = includeDirectories.ToArray(),
                AdditionalLibraries = libFiles.ToArray(),
                AdditionalPreprocessorMacros = Defines.Select(d => d.Definition).ToArray(),
            };

            if (framework.ID.StartsWith(FrameworkIDPrefix))
            {
                var shortID = framework.ID.Substring(FrameworkIDPrefix.Length);
                shortID = shortID.Replace("CMSIS_Driver_include", "dinc");
                shortID = shortID.Replace("_CMSISInclude", "_inc");
                if (shortID.StartsWith("platform."))
                    shortID = shortID.Replace("platform.", "p.");

                framework.ShortUniqueName = shortID;
            }

            if (specificDevice != null)
            {
                framework.ClassID = framework.ID;
                framework.ID += "." + specificDevice.FamilyID;
            }

            return framework;
        }

    }
}
