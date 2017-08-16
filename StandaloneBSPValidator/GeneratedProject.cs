using BSPEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StandaloneBSPValidator
{
    class GeneratedProject
    {
        readonly string _ProjectDir;
        List<string> _SourceFiles = new List<string>();
        readonly LoadedBSP.ConfiguredMCU MCU;
        List<EmbeddedFramework> _Frameworks = new List<EmbeddedFramework>();

        public IEnumerable<string> SourceFiles { get { return _SourceFiles; } }

        public GeneratedProject(string projectDir, LoadedBSP.ConfiguredMCU mcu, string[] selectedFrameworks)
        {
            _ProjectDir = projectDir;

            MCU = mcu;

            if (MCU.BSP.BSP.Frameworks != null)
                foreach (var fw in MCU.BSP.BSP.Frameworks)
                {
                    if (!selectedFrameworks.Contains(fw.ID))
                    {
                        if (fw.ClassID != null && selectedFrameworks.Contains(fw.ClassID))
                        {
                            try
                            {
                                if (!fw.IsCompatibleWithMCU(MCU.ExpandedMCU.ID))
                                    continue;
                            }
                            catch
                            {
                                continue;
                            }
                        }
                        else
                            continue;
                    }

                    _Frameworks.Add(fw);
                }
        }

        public GeneratedProject(LoadedBSP.ConfiguredMCU mcu, VendorSample vs, string projectDir, Dictionary<string, string> bspDict, string[] frameworks)
            : this(projectDir, mcu, frameworks)
        {
            _ProjectDir = projectDir;

            _SourceFiles.AddRange(vs.SourceFiles.Select(s=>VariableHelper.ExpandVariables(s, bspDict)));
        }

        public void DoGenerateProjectFromEmbeddedSample(ConfiguredSample sample, bool plainC, Dictionary<string, string> bspDict)
        {
            Dictionary<string, bool> binaryFiles = new Dictionary<string, bool>(StringComparer.CurrentCultureIgnoreCase);
            Dictionary<string, bool> ignoredFiles = new Dictionary<string, bool>(StringComparer.CurrentCultureIgnoreCase);
            var data = sample.Sample.Sample;
            if (data.IgnoredFiles != null)
                foreach (var fn in data.IgnoredFiles)
                    ignoredFiles[Path.Combine(sample.Sample.Directory, fn)] = true;
            if (data.BinaryFiles != null)
                foreach (var fn in data.BinaryFiles)
                    binaryFiles[Path.Combine(sample.Sample.Directory, fn)] = true;

            ignoredFiles[Path.Combine(sample.Sample.Directory, LoadedBSP.SampleDescriptionFile)] = true;
            DoImportSampleRecursively(bspDict, sample, sample.Sample.Directory, _ProjectDir, binaryFiles, ignoredFiles, plainC, "");
            if (data.AdditionalSourcesFromBSP != null)
                AddBSPFilesRecursively(data.AdditionalSourcesFromBSP, bspDict, _ProjectDir);
            if (data.AdditionalSourcesToCopy != null)
                foreach (var fobj in data.AdditionalSourcesToCopy)
                {
                    string sourceFile = VariableHelper.ExpandVariables(fobj.SourcePath, bspDict);
                    string destFile = Path.Combine(_ProjectDir, VariableHelper.ExpandVariables(fobj.TargetFileName ?? Path.GetFileName(sourceFile), bspDict));
                    File.Copy(sourceFile, destFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile));

                    string ext = Path.GetExtension(destFile).ToLower();
                    if (ext != ".h" && ext != ".hpp")
                        _SourceFiles.Add(destFile);
                }
        }

        public void AddBSPFilesToProject(Dictionary<string, string> SystemDictionary, Dictionary<string,string> frameworkConfig, Dictionary<string, bool> frameworkIDs)
        {
            if (MCU.ExpandedMCU.AdditionalSourceFiles != null && MCU.ExpandedMCU.AdditionalSourceFiles.Length > 0)
            {
                foreach (var fn in MCU.ExpandedMCU.AdditionalSourceFiles)
                {
                    if (MCU.BSP.ShouldSkipFile(fn, SystemDictionary, null, frameworkIDs))
                        continue;

                    var expandedFN = VariableHelper.ExpandVariables(fn, SystemDictionary);
                    try
                    {
                        if (fn.StartsWith("*"))
                            expandedFN = expandedFN.Substring(1);
                        string fullPath = Path.GetFullPath(expandedFN);
                        _SourceFiles.Add(fullPath);
                    }
                    catch { }
                }
            }

            foreach (var fw in _Frameworks)
            {
                var files = fw.AdditionalSourceFiles.Where(fn => !MCU.BSP.ShouldSkipFile(fn, SystemDictionary, frameworkConfig, frameworkIDs)).Select(fn => VariableHelper.ExpandVariables(fn, SystemDictionary, frameworkConfig));
                _SourceFiles.AddRange(files);
            }
        }
        public void AddBSPFilesToProject(List<string> pSrcFile,string pDirPrj)
        {
            foreach(var sp in pSrcFile)
            {
                 string fullPath = Path.Combine(pDirPrj,sp);
                 _SourceFiles.Add(fullPath);
            }
        }

        void DoImportSampleRecursively(Dictionary<string, string> bspDict, ConfiguredSample sample, string sourceDir, string targetDir, Dictionary<string, bool> binaryFiles, Dictionary<string, bool> ignoredFiles, bool plainC, string relativePath)
        {
            foreach (var fn in Directory.GetFiles(sourceDir))
            {
                try
                {
                    string target = Path.Combine(targetDir, Path.GetFileName(fn));
                    if (!plainC && Path.GetExtension(target).ToLower() == ".c" && !sample.Sample.Sample.DoNotUpgradeCToCpp)
                        target = Path.ChangeExtension(target, ".cpp");
                    else if (plainC && Path.GetExtension(target).ToLower() == ".cpp")
                        target = Path.ChangeExtension(target, ".c");

                    if (target.Contains("$$"))
                        target = VariableHelper.ExpandVariables(target, bspDict, sample.Parameters);

                    if (ignoredFiles.ContainsKey(fn))
                        continue;
                    if (binaryFiles.ContainsKey(fn))
                        File.Copy(fn, target);
                    else
                    {
                        string str = File.ReadAllText(fn);

                        str = VariableHelper.ExpandVariables(str, bspDict, sample.Parameters);
                        File.WriteAllText(target, str);
                    }

                    string ext = Path.GetExtension(target).ToLower();
                    if (ext != ".h" && ext != ".hpp")
                        _SourceFiles.Add(target);
                }
                catch
                {
                }
            }
            foreach (var fn in Directory.GetDirectories(sourceDir))
            {
                string target;
                try
                {
                    target = Path.Combine(targetDir, Path.GetFileName(fn));
                    Directory.CreateDirectory(target);
                }
                catch
                {
                    continue;
                }
                DoImportSampleRecursively(bspDict, sample, fn, target, binaryFiles, ignoredFiles, plainC, relativePath + Path.GetFileName(fn) + "\\");
            }
        }

        void AddBSPFilesRecursively(VirtualSourceDir dir, Dictionary<string, string> systemDict, string projectDir)
        {
            if (dir.Files != null)
                foreach (var fn in dir.Files)
                {
                    string fullPath = VariableHelper.ExpandVariables(fn, systemDict);
                    _SourceFiles.Add(fullPath);
                }
            if (dir.Subdirs != null)
                foreach (var subdir in dir.Subdirs)
                    AddBSPFilesRecursively(subdir, systemDict, projectDir);
        }

        public const string MapFileName = "test.map";
        public bool DataSections;

        internal ToolFlags GetToolFlags(Dictionary<string, string> systemDict, Dictionary<string, string> frameworkDict, IDictionary frameworkIDs)
        {
            var flags = new ToolFlags { CXXFLAGS = "-fno-exceptions -ffunction-sections -Os", LDFLAGS = "-Wl,-gc-sections -Wl,-Map," + MapFileName, CFLAGS = "-ffunction-sections -Os" };
            if (DataSections)
            {
                flags.CXXFLAGS += " -fdata-sections";
                flags.CFLAGS += " -fdata-sections";
            }

            var mcuFlags = MCU.ExpandToolFlags(systemDict, null);
            flags = flags.Merge(mcuFlags);
            Dictionary<string, string> primaryDict = new Dictionary<string, string>(systemDict);

            foreach (var fwObj in _Frameworks)
            {
                if (fwObj.AdditionalSystemVars != null) 
                    foreach (var sv in fwObj.AdditionalSystemVars)
                        primaryDict[sv.Key] = sv.Value;

                flags.IncludeDirectories = LoadedBSP.Combine(flags.IncludeDirectories, VariableHelper.ExpandVariables(fwObj.AdditionalIncludeDirs, primaryDict, frameworkDict));
                flags.PreprocessorMacros = LoadedBSP.Combine(flags.PreprocessorMacros, VariableHelper.ExpandVariables(fwObj.AdditionalPreprocessorMacros, primaryDict, frameworkDict)?.Where(m => !string.IsNullOrEmpty(m))?.ToArray());
            }

            if (MCU.BSP.BSP.ConditionalFlags != null)
            {
                foreach (var cf in MCU.BSP.BSP.ConditionalFlags)
                    if (cf.FlagCondition.IsTrue(primaryDict, frameworkDict, frameworkIDs))
                        flags = flags.Merge(LoadedBSP.ConfiguredMCU.ExpandToolFlags(cf.Flags, primaryDict, frameworkDict));
            }
            return flags;
        }
    }
}
