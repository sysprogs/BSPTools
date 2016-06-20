/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
   This software is licensed under the Sysprogs BSP Generator License.
   https://github.com/sysprogs/BSPTools/blob/master/LICENSE
*/
 
using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

/*
 * Overall file structure:
 *  Each family (with significantly different settings) must be defined in a separate XML file containing FamilyDefinition.
 *  Each family must define a DeviceRegex that will also be applied to all frameworks listed in the family.
 *  PrimaryHeaderDir/StartupFileDir are not used by the BSPGenerationTools and are provided for use in the specific generator.
 *  Each family defines a list of copy jobs and a list of common settings (e.g. CFLAGS).
 *  Copy jobs actually automate a lot of things:    
 *      1. Actually copy files to the BSP directory
 *      2. Add copied files to the VS project (filters apply)
 *      3. Automatically discover .h files and add their dirs to include dir list (filters apply)
 *      4. Apply patches to copied files (not GNU diff patches, just a basic homebrew mechanism)
 *      
 * Each family may define one or more MCU classifiers. An MCU classifier is essentially a nice way of defining something like this:
 *  switch(MCUName) {
 *      case "STM32F100VB":
 *      case "STM32F100VG":
 *          SystemVars["family"] = "F100V";
 *          break;
 *      case "xxx":
 *          SystemVars["family"] = "yyy";
 *          break;
 *  }
 *  
 * A family can define several MCU classifiers (e.g. one for HAL libraries, one for StdPeriph libraries). 
 * If a classifier is declared as required, the BSP generator framework will check that all MCUs are covered by it and throw otherwise.
 * A classifier can be declared primary if the generator tool wants to distinguish it from other classifiers. The framework ignores the 'primary' flag.
 * 
 *  Common settings can be defined in a shared FamilyDefinition object.
 */

namespace BSPGenerationTools
{
    class CopyFilters
    {
        List<KeyValuePair<Regex, bool>> Rules = new List<KeyValuePair<Regex, bool>>();

        public static KeyValuePair<Regex, bool> FileMaskToRegexWithFlag(string mask, bool ignoreCase = false)
        {
            // Clean up invalid values
            if (String.IsNullOrEmpty(mask) || mask == "-" || mask == "-\"\"")
                return new KeyValuePair<Regex, bool>(null, true);

            bool include = true;
            if (mask.StartsWith("-"))
            {
                include = false;
                mask = mask.Substring(1);
            }

            if (mask.Length >= 2 && mask.StartsWith("\"") && mask.EndsWith("\""))
                mask = mask.Substring(1, mask.Length - 2);

            return new KeyValuePair<Regex, bool>(BSPEngine.WildcardHelper.WildcardToRegex(mask, ignoreCase), include);
        }

        public CopyFilters(string rules)
        {
            if (!string.IsNullOrEmpty(rules))
            {
                foreach (var r in rules.Split(';'))
                {
                    var rule = FileMaskToRegexWithFlag(r.Replace('/', '\\'), true);
                    if (rule.Key == null)
                        throw new Exception("Empty rule");
                    Rules.Add(rule);
                }
            }
        }

        public bool IsMatch(string str)
        {
            foreach (var kv in Rules)
                if (kv.Key.IsMatch(str))
                    return kv.Value;
            return false;
        }
    }

    [XmlInclude(typeof(Patch.InsertLines))]
    [XmlInclude(typeof(Patch.ReplaceLine))]
    [XmlInclude(typeof(Patch.RegexTransform))]
    public abstract class Patch
    {
        public string FilePath;
        public string TargetPath;

        public abstract void Apply(List<string> lines);
        public class InsertLines : Patch
        {
            public string AfterLine;
            public string[] InsertedLines;

            public override void Apply(List<string> lines)
            {
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i] == AfterLine)
                    {
                        lines.InsertRange(i + 1, InsertedLines);
                        found = true;
                        break;
                    }
                }
                if (!found)
                    throw new Exception("Failed to apply patch for " + FilePath);
            }
        }

        public class ReplaceLine : Patch
        {
            public string OldLine;
            public string NewLine;

            public override void Apply(List<string> lines)
            {
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i] == OldLine)
                    {
                        lines[i] = NewLine;
                        found = true;
                        break;
                    }
                }
                if (!found)
                    throw new Exception("Failed to apply patch for " + FilePath);
            }
        }
        public class RegexTransform : Patch
        {
            public string RegularExpression;
            public string ValueFormat;
            public int ExpectedCount = 1;

            public override void Apply(List<string> lines)
            {
                var rg = new Regex(RegularExpression);
                int hitCount = 0;
                for (int i = 0; i < lines.Count; i++)
                {
                    var m = rg.Match(lines[i]);
                    if (m.Success)
                    {
                        List<object> args = new List<object>();
                        foreach(Group g in m.Groups)
                            args.Add(g.Value);
                        lines[i] = string.Format(lines[i], args.ToArray());
                        hitCount++;
                    }
                }

                if (hitCount != ExpectedCount)
                    throw new Exception("Unexpected hit count for patch");
            }
        }
    }

    //<MASKLIST> mentioned below is a semicolon-separated list of file masks (not regexes). The masks are applied to the relative path of each file.
    //Specify '-' in front, to make a negative mask. The masks are evaluated in the order they are specified until a matching mask is found.
    //Example:
    //    *.h;-subdir\*;*.c;readme.txt
    //This will include:
    //      test.h
    //      subdir\test.h (*.h is checked before subdir\*)
    //      test.c
    //      readme.txt
    //This will exclude:
    //      subdir\test.c (subdir\* is checked before *.c)
    //      test.exe      (no rule matches)
    //      dir\readme.txt ("readme.txt" is applied to the entire path, use *\readme.txt to for 'from any dir' semantics)

    //<CONDITION SYNTAX> used in SimpleFileConditions field:
    //  <file REGEX (with double \\)>: <condition>
    //  <condition> can be:
    //      a == b
    //      a != b
    //      a =~ regex
    //  E.g. 
    //      Class\\AUDIO\\: $$com.sysprogs.bspoptions.stm32.usb.devclass$$ == AUDIO
    //  means:
    //      ALL files from the current job containing "Class\Audio" in the path will be included ONLY if com.sysprogs.bspoptions.stm32.usb.devclass is set to "AUDIO".
    // The SimpleFileConditions uses a very limited syntax (e.g. no support for AND/OR) and is compiled into more flexible BoardSupportPackage.Condition objects.
    // If the necessity arises to support more complex condition syntax by the generator, either update the syntax above, or add support for specifying the full
    // Condition objects. As the condition string parsing happens here and not by the BSPEngine, extending the syntax won't break backward compatibility.
    // The final BSP contains one global condition table mentioning all files from the entire BSP. This enables a simple dictionary lookup to check whether
    // a file is conditional.

    public class CopyJob
    {
        public string SourceFolder; //full path
        public string TargetFolder; //if null, defaults to (BSP)\(name of source folder), otherwise specifies relative path within the family subdirectory
        public string FilesToCopy;  //<MASKLIST> of files to copy

        public string AutoIncludeMask = "*.h";  //<MASKLIST> that will be used to derive include dirs
        public string ProjectInclusionMask = "*"; //<MASKLIST> that will be added in the Solution Explorer
        public string PreprocessorMacros;   //semicolon-separated macros
        public string AdditionalIncludeDirs;    //Semicolon-separated, full path starting with $$SYS:BSP_ROOT$$
        public string[] SimpleFileConditions;   //See <CONDITION SYNTAX> above
        public bool AlreadyCopied;  //The files have been copied (and patched) by some previous jobs. This job is defined only to add the files to the project.
        public string[] GuardedFiles;

        public Patch[] Patches;

        class ParsedCondition
        {
            public Regex Regex;
            public Condition Condition;
            public int UseCount;

            public override string ToString()
            {
                if (UseCount == 0)
                    return "UNUSED: " + Regex.ToString();
                else
                    return Regex.ToString();
            }
        }

        public ToolFlags CopyAndBuildFlags(BSPBuilder bsp, List<string> projectFiles, string subdir)
        {
            List<ParsedCondition> conditions = null;
            if (SimpleFileConditions != null)
            {
                conditions = new List<ParsedCondition>();
                foreach (var cond in SimpleFileConditions)
                {
                    int idx = cond.IndexOf(':');
                    if (idx == -1)
                        throw new Exception("Invalid simple condition format");

                    Regex rgFile = new Regex(cond.Substring(0, idx), RegexOptions.IgnoreCase);
                    string rawCond = cond.Substring(idx + 1).Trim();
                    Condition parsedCond = ParseCondition(rawCond);
                    conditions.Add(new ParsedCondition { Regex = rgFile, Condition = parsedCond });
                }
            }

            string expandedSourceFolder = SourceFolder;
            bsp.ExpandVariables(ref expandedSourceFolder);

            if (TargetFolder == null)
                TargetFolder = Path.GetFileName(expandedSourceFolder);
            TargetFolder = TargetFolder.Replace('\\', '/');
            if (subdir == null)
                subdir = "";
            string absTarget = Path.Combine(bsp.BSPRoot, subdir, TargetFolder);
            Directory.CreateDirectory(absTarget);

            string folderInsideBSPPrefix = TargetFolder;
            if (!string.IsNullOrEmpty(subdir))
                folderInsideBSPPrefix = subdir + "/" + TargetFolder;
            folderInsideBSPPrefix = folderInsideBSPPrefix.Replace('\\', '/');
            if (folderInsideBSPPrefix == "/")
                folderInsideBSPPrefix = "";
            else if (folderInsideBSPPrefix != "" && !folderInsideBSPPrefix.StartsWith("/"))
                folderInsideBSPPrefix = "/" + folderInsideBSPPrefix;
            
            var copyMasks = new CopyFilters(FilesToCopy);
            var autoIncludes = new CopyFilters(AutoIncludeMask);    
            var projectContents = new CopyFilters(ProjectInclusionMask);
            var filesToCopy = Directory.GetFiles(expandedSourceFolder, "*", SearchOption.AllDirectories).Select(f => f.Substring(expandedSourceFolder.Length + 1)).Where(f => copyMasks.IsMatch(f)).ToArray();
            foreach (var dir in filesToCopy.Select(f => Path.Combine(absTarget, Path.GetDirectoryName(f))).Distinct())
                Directory.CreateDirectory(dir);

            var includeDirs = filesToCopy.Where(f => autoIncludes.IsMatch(f)).Select(f => Path.GetDirectoryName(f).Replace('\\', '/')).Distinct().Select(d => "$$SYS:BSP_ROOT$$" + folderInsideBSPPrefix + (string.IsNullOrEmpty(d) ? "" : ("/" + d))).ToList();
            foreach (var f in filesToCopy)
            {
                string pathInsidePackage = Path.Combine(subdir, TargetFolder, f);
                if (pathInsidePackage.Length > 120)
                {
                    if (!bsp.OnFilePathTooLong(pathInsidePackage))
                        continue;
                }

                string targetFile = Path.Combine(absTarget, f);
                if (AlreadyCopied)
                {
                    if (!File.Exists(targetFile))
                        throw new Exception(targetFile + " required by a copy job marked as 'Already Copied' does not exist");
                }
                else
                    File.Copy(Path.Combine(expandedSourceFolder, f), targetFile, true);

                File.SetAttributes(targetFile, File.GetAttributes(targetFile) & ~FileAttributes.ReadOnly);
                string encodedPath = "$$SYS:BSP_ROOT$$" + folderInsideBSPPrefix + "/" + f.Replace('\\', '/');

                if (projectContents.IsMatch(f))
                    projectFiles.Add(encodedPath.Replace('\\', '/'));

                if (conditions != null)
                {
                    foreach (var cond in conditions)
                        if (cond.Regex.IsMatch(f))
                        {
                            bsp.MatchedFileConditions.Add(new FileCondition { ConditionToInclude = cond.Condition, FilePath = encodedPath });
                            cond.UseCount++;
                            break;
                        }
                }
            }

            var unusedConditions = conditions?.Where(c => c.UseCount == 0)?.ToArray();
            if ((unusedConditions?.Length ?? 0) != 0)
                throw new Exception(string.Format("Found {0} unused conditions. Please recheck your rules.", unusedConditions.Length));

            if (Patches != null)
                foreach(var p in Patches)
                {
                    List<string> allLines = File.ReadAllLines(Path.Combine(absTarget, p.FilePath)).ToList();
                    p.Apply(allLines);

                    string targetPath = p.TargetPath;
                    if (targetPath == null)
                        targetPath = p.FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(absTarget, targetPath)));
                    File.WriteAllLines(Path.Combine(absTarget, targetPath), allLines);
                }

            if (GuardedFiles != null)
                foreach (var gf in GuardedFiles)
                {
                    int idx = gf.IndexOf("=>");
                    Regex rgFile = new Regex(gf.Substring(0, idx));
                    string macro = gf.Substring(idx + 2);
                    var fn = Path.Combine(absTarget, filesToCopy.First(f => rgFile.IsMatch(f)));

                    List<string> lines = new List<string>(File.ReadAllLines(fn));
                    int i = 0;
                    //1. Find first #include
                    for (i = 0; i < lines.Count; i++)
                        if (lines[i].Trim().StartsWith("#include"))
                            break;

                    //2. Find first non-preprocessor line
                    for (; i < lines.Count; i++)
                        if (!string.IsNullOrWhiteSpace(lines[i]) && !lines[i].Trim().StartsWith("#include"))
                            break;

                    if (i == lines.Count)
                        throw new Exception("Cannot find a place to insert guard in " + fn);

                    lines.Insert(i, string.Format("#if defined({0}) && {0}", macro));
                    lines.Add("#endif //" + macro);
                    File.WriteAllLines(fn, lines);
                }

            if (AdditionalIncludeDirs != null)
                includeDirs.AddRange(AdditionalIncludeDirs.Split(';'));

            return new ToolFlags
            {
                PreprocessorMacros = (PreprocessorMacros == null) ? null : PreprocessorMacros.Split(';'),
                IncludeDirectories = includeDirs.ToArray()
            };
        }

        private Condition ParseCondition(string rawCond)
        {
            int idx = rawCond.IndexOf("&&");
            if (idx != -1)
                return new Condition.And { Arguments = new Condition[] { ParseCondition(rawCond.Substring(0, idx).Trim()), ParseCondition(rawCond.Substring(idx + 2).Trim()) } };

            if (rawCond.StartsWith("fw:"))
                return new Condition.ReferencesFramework { FrameworkID = rawCond.Substring(3).Trim() };

            idx = rawCond.IndexOf("==");
            if (idx != -1)
                return new Condition.Equals { Expression = rawCond.Substring(0, idx).Trim(), ExpectedValue = rawCond.Substring(idx + 2).Trim() };
            else if ((idx = rawCond.IndexOf("=~")) != -1)
                return new Condition.MatchesRegex { Expression = rawCond.Substring(0, idx).Trim(), Regex = rawCond.Substring(idx + 2).Trim() };
            else if ((idx = rawCond.IndexOf("!=")) != -1)
                return new Condition.Not { Argument = new Condition.Equals { Expression = rawCond.Substring(0, idx).Trim(), ExpectedValue = rawCond.Substring(idx + 2).Trim() } };
            else
                throw new Exception("Cannot parse simple condition");
        }
    }

    public class Framework
    {
        public string ID;           //Unique ID of the framework (e.g. stm32f4_hal). Must be unique!
        public string ClassID;      //Generalized ID of the framework (e.g. stm32_hal). Can be referenced by samples to select any framework with the matching class ID that is compatible with the current device.
        public string Name;
        public string[] RequiredFrameworks; //IDs or ClassIDs of required frameworks
        public CopyJob[] CopyJobs;
        public bool DefaultEnabled = true;  //Specifies whether the framework will be enabled when opening an older project that does not have any framework refs.
        public string ProjectFolderName;    //Subfolder name in solution explorer where the framework files will be placed
        public string Filter;               //A regex specifying compatible MCUs
        public PropertyList ConfigurableProperties; //Framework options - will be shown on the sample page of the wizard and on the Embedded Frameworks page
        public SysVarEntry[] AdditionalSystemVars;  //Additional vars that can be referenced in sample templates
        public string[] IncompatibleFrameworks; //Mutually exclusive frameworks (e.g. HAL is incompatible with StdPeriph)
    }

    //Smart, i.e. configurable via wizard. We will eventually support 'dumb' samples just cloned from the BSP "as is".
    public class SmartSample
    {
        public string SourceFolder; //Full path (or relative to the working dir of the generator)
        public string DestinationFolder;    //Relative path under BSP
        public string[] AdditionalSources;  //Full paths ($$SYS:BSP_ROOT$$/...) of additional files that will be copied to the project dir. Can use the path/a.c=>b.c syntax to rename the file to b.c
        public string MCUFilterRegex;
        public bool IsTestProjectSample;
    }

    /* Use MCU classifiers to define MCU-specific flags.
     * E.g. if MCUA1 and MCUA2 must define -DMYMCUAx and MCUB1 and MCUB2 must define -DMYMCUBx, then:
     *      1. Create a classifier 
        	    <MCUClassifier>
			        <VariableName>com.sysprogs.myclassifier</VariableName>
    			    <AutoOptions>MCUAx;MCUBx</AutoOptions>
	    		    <Options/>
        	    <MCUClassifier>
     *      2. In one of the family's copy jobs define preprocessor a macro:
				<PreprocessorMacros>MY$$com.sysprogs.myclassifier$$</PreprocessorMacros>
     * 
     *      MCU classifiers use variables instead of just defining macros to allow more flexibility, e.g.:
     *          * Referencing classifiers in file conditions
     *          * Referencing classifiers in file templates in smart samples
     */
    public class MCUClassifier
    {
        public string VariableName;     //System variable that will receive the value
        public bool Required;
        public bool IsPrimary;
        public string UnsupportedMCUs;  //The framework will throw if Required is true and some of the MCUs are not covered by this classifier and not listed here.
        public string AutoOptions;      //Comma-separated list of device names where 'x' is used as a wildcard (e.g. STM32F100xx will be converted to a "STM32F100.." regex)
        public Option[] Options;        //Value/regex pair for the options that cannot be defined using the simplified AutoOptions format.

        public class Option
        {
            public string Value;
            public string Regex;
        }

        List<KeyValuePair<Regex, string>> _Cache;

        public string TryMatchMCUName(string name)
        {
            if (_Cache == null)
            {
                _Cache = new List<KeyValuePair<Regex, string>>();
                foreach (var op in Options)
                    _Cache.Add(new KeyValuePair<Regex, string>(new Regex(op.Regex), op.Value));
                if (AutoOptions != null)
                    foreach(var ao in AutoOptions.Split(';'))
                        _Cache.Add(new KeyValuePair<Regex, string>(new Regex(ao.Replace('x', '.')), ao));
            }

            foreach (var kv in _Cache)
                if (kv.Key.IsMatch(name))
                    return kv.Value;

            return null;
        }
    }

    public class FrameworkTemplate
    {
        public string Range;
        public Framework Template;

        //Warning: the expansion is not complete and should be updated as needed
        public IEnumerable<Framework> Expand()
        {
            XmlSerializer ser = new XmlSerializer(typeof(Framework));
            MemoryStream ms = new MemoryStream();
            ser.Serialize(ms, Template);

            foreach (var n in Range.Split(' '))
            {
                ms.Seek(0, SeekOrigin.Begin);
                Framework deepCopy = (Framework)ser.Deserialize(ms);
                Expand(ref deepCopy.Name, n);
                Expand(ref deepCopy.ID, n);
                Expand(ref deepCopy.ProjectFolderName, n);
                foreach (var job in deepCopy.CopyJobs)
                {
                    Expand(ref job.SourceFolder, n);
                    Expand(ref job.TargetFolder, n);
                    Expand(ref job.FilesToCopy, n);
                }
                yield return deepCopy;
            }
        }

        static void Expand(ref string str, string name)
        {
            str = str.Replace("$$BSPGEN:FRAMEWORK$$", name);
        }
    }

    public class FamilyDefinition
    {
        public string Name;
        public string DeviceRegex;
        public string FamilySubdirectory;
        public string PrimaryHeaderDir;
        public string StartupFileDir;
        public bool HasMixedCores; //Different devices within the family can have different cores (e.g. Cortex-M0 vs Cortex-M4)
        public Framework CoreFramework;

        public Framework[] AdditionalFrameworks;
        public SmartSample[] SmartSamples;
        public MCUClassifier[] Subfamilies;
        public ToolFlags CompilationFlags = new ToolFlags();
        public SysVarEntry[] AdditionalSystemVars;
        public PropertyList ConfigurableProperties;
        public ConditionalToolFlags[] ConditionalFlags;
        public FrameworkTemplate[] AdditionalFrameworkTemplates;
    }
}
