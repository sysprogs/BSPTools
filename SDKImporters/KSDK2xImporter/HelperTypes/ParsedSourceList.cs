using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace KSDK2xImporter.HelperTypes
{
    public enum SourceType
    {
        Unknown,
        Header,
        Source,
        Library,
        LinkerScript,
    }

    public struct FileReference
    {
        public readonly string RelativePath;
        public readonly SourceType Type;

        public FileReference(string relativePath, SourceType type)
        {
            RelativePath = relativePath;
            Type = type;
        }

        public string GetLocalPath(string baseDirectory) => Path.Combine(baseDirectory, RelativePath);

        public string GetBSPPath()
        {
            if (RelativePath == null)
                return null;
            return "$$SYS:BSP_ROOT$$/" + RelativePath.Replace('\\', '/');
        }

        public override string ToString() => RelativePath;

        public void UpdateIncludeDirectoryList(HashSet<string> includeDirectories)
        {
            if (Type == SourceType.Header)
            {
                var bspPath = GetBSPPath();
                int idx = bspPath.LastIndexOf('/');
                if (idx != -1)
                    includeDirectories.Add(bspPath.Substring(0, idx));
            }
        }
    }

    class ParsedSourceList
    {
        //WARNING: all paths and masks can include variables, such as $|core|
        public readonly string SourcePath;
        public readonly SourceType Type;

        public readonly string ExtraCondition;
        public readonly ParsedFilter Filter;

        public readonly string[] Masks;

        public ParsedSourceList(XmlElement e, string basePath)
        {
            SourcePath = e.GetAttribute("path");
            if (string.IsNullOrEmpty(SourcePath))
            {
                var relPath = e.GetAttribute("relative_path");
                if (!string.IsNullOrEmpty(relPath) && !string.IsNullOrEmpty(basePath))
                {
                    if (relPath.Trim('/') == ".")
                        SourcePath = basePath;
                    else
                        SourcePath = $"{basePath}/{relPath}";
                }
            }

            //TargetPath = e.GetAttribute("target_path");
            ExtraCondition = e.GetAttribute("condition");
            Filter = new ParsedFilter(e);

            Masks = e.SelectNodes("files/@mask").OfType<XmlAttribute>().Select(a => a.Value).Where(s => !string.IsNullOrEmpty(s)).ToArray();

            Type = e.GetAttribute("type") switch
            {
                "c_include" => SourceType.Header,
                "src" => SourceType.Source,
                "asm_include" => SourceType.Source,
                "lib" => SourceType.Library,
                "linker" => SourceType.LinkerScript,
                _ => SourceType.Unknown,
            };
        }


        public IEnumerable<FileReference> LocateAllFiles(SpecializedDevice device, string rootDir)
        {
            var expandedPath = SpecializedDevice.ExpandVariables(SourcePath, device).Replace('\\', '/');

            foreach (var mask in Masks)
            {
                var expandedMask = SpecializedDevice.ExpandVariables(mask, device);
                if (mask.Contains("|") || expandedPath.Contains("|"))
                    continue;

                string[] foundFileNames;

                try
                {
                    if (mask.Contains("*"))
                        foundFileNames = Directory.GetFiles(Path.Combine(rootDir, expandedPath), mask).Select(f => Path.GetFileName(f)).ToArray();
                    else
                        foundFileNames = new string[] { mask };
                }
                catch
                {
                    foundFileNames = new string[0];
                }

                foreach (var fn in foundFileNames)
                {
                    string fullPath;
                    try
                    {
                        fullPath = Path.Combine(rootDir, expandedPath + "/" + fn);
                        if (!File.Exists(fullPath))
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    yield return new FileReference($"{expandedPath}/{fn}", Type);
                }
            }
        }


        //public string BSPPath => "$$SYS:BSP_ROOT$$/" + _Path.Replace('\\', '/');
    }

}
