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
    class ParsedSourceList
    {
        //WARNING: all paths and masks can include variables, such as $|core|
        public readonly string Path, TargetPath;
        public readonly string Type;

        public readonly string ExtraCondition;
        public readonly ParsedFilter Filter;

        public readonly string[] Masks;

        public ParsedSourceList(XmlElement e)
        {
            Path = e.GetAttribute("path");
            TargetPath = e.GetAttribute("target_path");
            ExtraCondition = e.GetAttribute("condition");
            Filter = new ParsedFilter(e);

            Masks = e.SelectNodes("files/@mask").OfType<XmlAttribute>().Select(a => a.Value).Where(s => !string.IsNullOrEmpty(s)).ToArray();
        }

        public struct FileReference
        {
            public string RelativePath;

            //public string BSPPath => "$$SYS:BSP_ROOT$$/" + RelativePath.Replace('\\', '/');
        }

        public IEnumerable<FileReference> LocateAllFiles(ConstructedBSPDevice device, string rootDir)
        {
            throw new NotImplementedException();
        }

#if !DEBUG
        public IEnumerable<FileReference> AllFiles
        {
            get
            {
                bool Exc = false;
                var toolchain = _Element.GetAttribute("toolchain") ?? "";
                if (toolchain != "" && !toolchain.Contains("armgcc"))
                    Exc = true;

                foreach (XmlAttribute maskAttr in _Element.SelectNodes("files/@mask"))
                {
                    if (Exc)
                        continue;
                    string mask = maskAttr.Value;
                    string[] items;
                    mask = ExpandVariables(mask);
                    if (mask.Contains("|") || _Path.Contains("|"))
                        continue;

                    try
                    {
                        if (mask.Contains("*"))
                            items = Directory.GetFiles(Path.Combine(_Device.SDKDirectory, _Path), mask).Select(f => Path.GetFileName(f)).ToArray();
                        else
                            items = new string[] { mask };
                    }
                    catch
                    {
                        items = new string[0];
                    }

                    foreach (var item in items)
                    {
                        string fullPath;
                        try
                        {
                            fullPath = Path.Combine(_Device.SDKDirectory, _Path + "/" + item);
                            if (!File.Exists(fullPath))
                                continue;
                        }
                        catch
                        {
                            continue;
                        }

                        yield return new FileReference { RelativePath = _Path + "/" + item, FullPath = fullPath };
                    }
                }
            }
        }
#endif

        //public string BSPPath => "$$SYS:BSP_ROOT$$/" + _Path.Replace('\\', '/');
    }

}
