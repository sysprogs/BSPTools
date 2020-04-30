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
        Library
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
    }

    class ParsedSourceList
    {
        //WARNING: all paths and masks can include variables, such as $|core|
        public readonly string SourcePath, TargetPath;
        public readonly SourceType Type;

        public readonly string ExtraCondition;
        public readonly ParsedFilter Filter;

        public readonly string[] Masks;

        public ParsedSourceList(XmlElement e)
        {
            SourcePath = e.GetAttribute("path");
            TargetPath = e.GetAttribute("target_path");
            ExtraCondition = e.GetAttribute("condition");
            Filter = new ParsedFilter(e);

            Masks = e.SelectNodes("files/@mask").OfType<XmlAttribute>().Select(a => a.Value).Where(s => !string.IsNullOrEmpty(s)).ToArray();

            Type = e.GetAttribute("type") switch
            {
                "c_include" => SourceType.Header,
                "src" => SourceType.Source,
                "asm_include" => SourceType.Source,
                "lib" => SourceType.Library,
                _ => SourceType.Unknown,
            };
        }


        public IEnumerable<FileReference> LocateAllFiles(SpecializedDevice device, string rootDir)
        {
            var expandedPath = device.ExpandVariables(SourcePath).Replace('\\', '/');

            foreach (var mask in Masks)
            {
                var expandedMask = device.ExpandVariables(mask);
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
