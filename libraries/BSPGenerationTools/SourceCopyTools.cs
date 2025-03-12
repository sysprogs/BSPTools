﻿/* Copyright (c) 2015 Sysprogs OU. All Rights Reserved.
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

            if (mask.StartsWith(">"))
                return new KeyValuePair<Regex, bool>(new Regex(mask.Substring(1), ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), include);
            else
                return new KeyValuePair<Regex, bool>(BSPEngine.WildcardHelper.WildcardToRegex(mask, ignoreCase), include);
        }

        public CopyFilters(string rules)
        {
            if (!string.IsNullOrEmpty(rules))
            {
                foreach (var r in rules.Split(';'))
                {
                    if (string.IsNullOrEmpty(r))
                        continue;
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

            public string AnchorLine;
            public int AnchorDistance = 1;

            public override void Apply(List<string> lines)
            {
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i] != OldLine)
                        continue;

                    if (AnchorLine != null)
                    {
                        int idx = i + AnchorDistance;
                        if (idx < 0 || idx >= lines.Count)
                            continue;

                        if (lines[idx] != AnchorLine)
                            continue;
                    }

                    lines[i] = NewLine;
                    found = true;
                    break;
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
                        foreach (Group g in m.Groups)
                            args.Add(g.Value);
                        lines[i] = string.Format(ValueFormat, args.ToArray());
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
        public string RenameRules;
        public string AdvancedRenameRules;  //regex=>expr

        public string AutoIncludeMask = "*.h";  //<MASKLIST> that will be used to derive include dirs
        public string ProjectInclusionMask = "*"; //<MASKLIST> that will be added in the Solution Explorer
        public string PreprocessorMacros;   //semicolon-separated macros
        public string AdditionalIncludeDirs;    //Semicolon-separated, full path starting with $$SYS:BSP_ROOT$$

        public string[] SimpleFileConditions;   //See <CONDITION SYNTAX> above

        public string SmartPropertyGroup;   //Syntax: [Group ID]|[Name]
        public string[] AutoSmartFileConditions; 
        public string[] SmartFileConditions; //Will be automatically translated to SimpleFileConditions & properties. Option name|list of (regex => option value). See CC3220 BSP for examples.
        public string[] SmartPreprocessorMacros;

        public bool AlreadyCopied;  //The files have been copied (and patched) by some previous jobs. This job is defined only to add the files to the project.
        public string[] GuardedFiles;
        public string GuardFormat;
        public string SymlinkResolutionMask;

        public Patch[] Patches;

        public string AdditionalProjectFiles;
        public string VendorSpecificAttributes;
        public CopyJobFlags Flags;
        public string SmartConditionsPromotedToPreprocessorMacros;
        public string TemplateFileSpec;
        public bool ExcludeFromVendorSampleMapping;
        public bool OrthogonalConditions;

        [Flags]
        public enum CopyJobFlags
        {
            None = 0,
            SimpleFileConditionsAreSecondary,
        }

        class ParsedCondition
        {
            public Regex Regex;
            public Condition Condition;
            public int UseCount;

            public ReverseFileConditionBuilder.ConditionHandle ReverseConditionHandle;

            public override string ToString()
            {
                if (UseCount == 0)
                    return "UNUSED: " + Regex.ToString();
                else
                    return Regex.ToString();
            }
        }

        interface IRenameRule
        {
            bool Matches(string targetFile);
            string Apply(string targetFile);
        }

        public class RenameRule : IRenameRule
        {
            public string OldName;
            public string NewName;

            public string Apply(string targetFile) => NewName;

            public bool Matches(string targetFile)
            {
                return StringComparer.InvariantCultureIgnoreCase.Compare(targetFile, OldName) == 0;
            }
        }

        public class AdvancedRenamingRule : IRenameRule
        {
            public Regex OldName;
            public string NewNameFormat;

            public string Apply(string targetFile)
            {
                return string.Format(NewNameFormat, OldName.Match(targetFile).Groups.OfType<object>().ToArray());
            }

            public bool Matches(string targetFile) => OldName.IsMatch(targetFile);
        }

        struct ConditionRecord
        {
            public string Condition;
            public ReverseFileConditionBuilder.ConditionHandle Handle;

            public ConditionRecord(string condition, ReverseFileConditionBuilder.ConditionHandle handle)
            {
                Condition = condition;
                Handle = handle;
            }
        }

        public ToolFlags CopyAndBuildFlags(BSPBuilder bsp,
            List<string> projectFiles, 
            string subdir, 
            ref PropertyList configurableProperties,
            ReverseFileConditionBuilder.Handle reverseConditions, 
            List<ConfigurationFileTemplate> configFiles,
            CopiedFileMonitor copiedFileMonitor)
        {
            List<ParsedCondition> conditions = null;
            List<ConditionRecord> allConditions = new List<ConditionRecord>();
            List<string> preprocessorMacros = new List<string>();

            Regex configTemplateRegex = TemplateFileSpec == null ? null : new Regex(TemplateFileSpec);

            if (SimpleFileConditions != null)
            {
                allConditions.AddRange(SimpleFileConditions.Select(c => new ConditionRecord(c, null)));
                if ((Flags & CopyJobFlags.SimpleFileConditionsAreSecondary) == CopyJobFlags.None)
                    reverseConditions?.FlagIncomplete(ReverseFileConditionWarning.HasRegularConditions);
            }

            if (!string.IsNullOrEmpty(PreprocessorMacros))
            {
                preprocessorMacros.AddRange(PreprocessorMacros.Split(';'));
                foreach (var macro in preprocessorMacros)
                    reverseConditions?.AttachPreprocessorMacro(macro, null);
            }

            string expandedSourceFolder = SourceFolder;
            bsp.ExpandVariables(ref expandedSourceFolder);

            var copyMasks = new CopyFilters(FilesToCopy);
            var projectContents = new CopyFilters(ProjectInclusionMask);

            var filesToCopy = Directory.GetFiles(expandedSourceFolder, "*", SearchOption.AllDirectories)
                .Where(f => !bsp.SkipHiddenFiles || (File.GetAttributes(f) & FileAttributes.Hidden) != FileAttributes.Hidden)
                .Select(f => f.Substring(expandedSourceFolder.Length + 1))
                .Where(f => copyMasks.IsMatch(f))
                .ToArray();

            if (AutoSmartFileConditions != null)
            {
                List<string> computedConditions = new List<string>();

                if (SmartFileConditions != null)
                    throw new Exception($"The copy job for {SourceFolder} defines AutoSmartFileConditions and hence cannot define regular SmartFileConditions");

                foreach(var cond in AutoSmartFileConditions)
                {
                    const string marker = "(***)";
                    int idx = cond.IndexOf(marker);
                    if (idx == -1)
                        throw new Exception($"The '{cond}' condition must contain the '{marker}' marker");

                    var regex = new Regex(cond.Replace(marker, "(.+)"), RegexOptions.IgnoreCase);
                    if (regex.GetGroupNames().Length != 2)  //one extra group for the 'whole regex'
                        throw new Exception($"The '{regex}' regex should only contain 1 group");

                    var groupNames = filesToCopy.Where(projectContents.IsMatch)
                        .Select(f => regex.Match(f))
                        .Where(m => m.Success)
                        .Select(m => m.Groups[1].Value)
                        .Distinct()
                        .ToArray();

                    foreach (var gn in groupNames)
                        computedConditions.Add($"-{gn}|" + cond.Replace(marker, gn));
                }

                SmartFileConditions = computedConditions.ToArray();
            }

            if (SmartFileConditions != null || SmartPreprocessorMacros != null)
            {
                var preproRegex = string.IsNullOrEmpty(SmartConditionsPromotedToPreprocessorMacros) ? null : new Regex(SmartConditionsPromotedToPreprocessorMacros);

                PropertyGroup grp;
                if (configurableProperties == null)
                    configurableProperties = new PropertyList { PropertyGroups = new List<PropertyGroup>() };
                if (!string.IsNullOrEmpty(SmartPropertyGroup))
                {
                    string[] elements = bsp.ExpandVariables(SmartPropertyGroup).Split('|');
                    var id = elements[0];

                    grp = configurableProperties.PropertyGroups.FirstOrDefault(g => g.UniqueID == id);
                    if (grp == null)
                        configurableProperties.PropertyGroups.Add(grp = new PropertyGroup { Name = elements[1], UniqueID = id });
                }
                else
                {
                    grp = configurableProperties.PropertyGroups.FirstOrDefault();
                    if (grp == null)
                        configurableProperties.PropertyGroups.Insert(0, grp = new PropertyGroup { });
                }

                var smartFileConditions = SmartFileConditions ?? new string[0];
                bsp.PatchSmartFileConditions(ref smartFileConditions, expandedSourceFolder, subdir, this);

                foreach (var str in smartFileConditions)
                {
                    var def = SmartPropertyDefinition.Parse(str, grp.UniqueID, 0, bsp.OnValueForSmartBooleanProperties);

                    if (def.Items.Length == 1)
                    {
                        var item = def.Items[0];
                        allConditions.Add(new ConditionRecord($"{item.Key}: $${def.IDWithPrefix}$$ == {item.Value.ID}", reverseConditions?.CreateSimpleCondition(def.IDWithPrefix, item.Value.ID)));

                        reverseConditions?.AttachMinimalConfigurationValue(def.IDWithPrefix, "");

                        grp.Properties.Add(new PropertyEntry.Boolean
                        {
                            ValueForTrue = item.Value.ID,
                            Name = def.Name,
                            UniqueID = def.IDWithoutPrefix,
                            DefaultValue = def.IsDefaultOn != false
                        });
                    }
                    else
                    {
                        List<PropertyEntry.Enumerated.Suggestion> suggestions = new List<PropertyEntry.Enumerated.Suggestion>();
                        int? emptyIndex = null;

                        foreach (var item in def.Items)
                        {
                            if (item.Key == "")
                            {
                                // 'None' value. No conditions will trigger when this is selected.
                                emptyIndex = suggestions.Count;
                            }
                            else
                            {
                                allConditions.Add(new ConditionRecord($"{item.Key}: $${def.IDWithPrefix}$$ == {item.Value.ID}", reverseConditions?.CreateSimpleCondition(def.IDWithPrefix, item.Value.ID)));
                            }

                            suggestions.Add(new PropertyEntry.Enumerated.Suggestion { InternalValue = item.Value.ID, UserFriendlyName = item.Value.Name });
                        }

                        reverseConditions?.AttachMinimalConfigurationValue(def.IDWithPrefix, suggestions[emptyIndex ?? def.DefaultItemIndex].InternalValue);

                        grp.Properties.Add(new PropertyEntry.Enumerated
                        {
                            Name = def.Name,
                            UniqueID = def.IDWithoutPrefix,
                            SuggestionList = suggestions.ToArray(),
                            DefaultEntryIndex = def.DefaultItemIndex,
                            DefaultEntryValue = def.DefaultValue,
                        });
                    }


                    if (preproRegex?.IsMatch(def.IDWithoutPrefix) == true)
                    {
                        preprocessorMacros.Add("$$" + def.IDWithPrefix + "$$");

                        if (def.Items.Length > 1)
                        {

                        }
                        else
                        {
                            var value = def.Items[0].Value.ID;
                            reverseConditions?.AttachPreprocessorMacro(value, reverseConditions?.CreateSimpleCondition(def.IDWithPrefix, value));
                            reverseConditions?.AttachMinimalConfigurationValue(def.IDWithPrefix, "");
                        }
                    }
                }

                foreach (var str in SmartPreprocessorMacros ?? new string[0])
                {
                    var def = SmartPropertyDefinition.Parse(str, grp.UniqueID, 1, bsp.OnValueForSmartBooleanProperties);
                    preprocessorMacros.Add(string.Format(def.ExtraArguments[0], "$$" + def.IDWithPrefix + "$$"));

                    if (def.Items.Length == 1)
                    {
                        var item = def.Items[0];

                        if (item.Key.StartsWith("@"))
                        {
                            var prop = new PropertyEntry.String
                            {
                                Name = def.Name,
                                UniqueID = def.IDWithoutPrefix,
                                DefaultValue = item.Key.TrimStart('@'),
                            };

                            grp.Properties.Add(prop);

                            reverseConditions?.AttachFreeformPreprocessorMacro(def.ExtraArguments[0], def.IDWithPrefix);
                            reverseConditions?.AttachMinimalConfigurationValue(def.IDWithPrefix, prop.DefaultValue);
                        }
                        else
                        {
                            grp.Properties.Add(new PropertyEntry.Boolean
                            {
                                Name = def.Name,
                                UniqueID = def.IDWithoutPrefix,
                                ValueForTrue = item.Key,
                                DefaultValue = def.IsDefaultOn ?? true,
                            });

                            string expandedMacro = string.Format(def.ExtraArguments[0], item.Key);
                            reverseConditions?.AttachPreprocessorMacro(expandedMacro, reverseConditions?.CreateSimpleCondition(def.IDWithPrefix, item.Key));
                            reverseConditions?.AttachMinimalConfigurationValue(def.IDWithPrefix, "");
                        }
                    }
                    else
                    {
                        List<PropertyEntry.Enumerated.Suggestion> suggestions = new List<PropertyEntry.Enumerated.Suggestion>();

                        foreach (var item in def.Items)
                        {
                            suggestions.Add(new PropertyEntry.Enumerated.Suggestion { InternalValue = item.Key, UserFriendlyName = item.Value.Name });

                            string expandedMacro = string.Format(def.ExtraArguments[0], item.Key);
                            reverseConditions?.AttachPreprocessorMacro(expandedMacro, reverseConditions?.CreateSimpleCondition(def.IDWithPrefix, item.Key));
                        }

                        reverseConditions?.AttachMinimalConfigurationValue(def.IDWithPrefix, suggestions[def.DefaultItemIndex].InternalValue);

                        grp.Properties.Add(new PropertyEntry.Enumerated
                        {
                            Name = def.Name,
                            UniqueID = def.IDWithoutPrefix,
                            SuggestionList = suggestions.ToArray(),
                            DefaultEntryIndex = def.DefaultItemIndex,
                            DefaultEntryValue = def.DefaultValue,
                        });
                    }
                }
            }

            if (allConditions.Count > 0)
            {
                conditions = new List<ParsedCondition>();
                foreach (var cond in allConditions)
                {
                    int idx = cond.Condition.IndexOf(':');
                    if (idx == -1)
                        throw new Exception("Invalid simple condition format");

                    Regex rgFile = new Regex(cond.Condition.Substring(0, idx), RegexOptions.IgnoreCase);
                    string rawCond = cond.Condition.Substring(idx + 1).Trim();
                    Condition parsedCond = ParseCondition(rawCond);
                    conditions.Add(new ParsedCondition { Regex = rgFile, Condition = parsedCond, ReverseConditionHandle = cond.Handle });
                }
            }

            if (TargetFolder == null)
                TargetFolder = Path.GetFileName(expandedSourceFolder);

            if (TargetFolder == "$$BSPGEN:RELPATH$$")
            {
                string prefix = "$$BSPGEN:INPUT_DIR$$\\";
                if (!SourceFolder.StartsWith(prefix))
                    throw new Exception("$$BSPGEN:RELPATH$$ can only be used together with " + prefix);
                TargetFolder = SourceFolder.Substring(prefix.Length);
            }

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

            var autoIncludes = new CopyFilters(AutoIncludeMask);
            var potentialSymlinks = new CopyFilters(SymlinkResolutionMask);

            foreach (var dir in filesToCopy.Select(f => Path.Combine(absTarget, Path.GetDirectoryName(f))).Distinct())
                Directory.CreateDirectory(dir);

            List<IRenameRule> rules = new List<IRenameRule>();
            foreach (var r in (RenameRules ?? "").Split(';').Select(s => s.Trim()).Where(s => s != ""))
            {
                int idx = r.IndexOf("=>");
                rules.Add(new RenameRule { OldName = r.Substring(0, idx), NewName = r.Substring(idx + 2) });
            }
            foreach (var r in (AdvancedRenameRules ?? "").Split(';').Where(s => s != ""))
            {
                int idx = r.IndexOf("=>");
                rules.Add(new AdvancedRenamingRule { OldName = new Regex(r.Substring(0, idx), RegexOptions.IgnoreCase), NewNameFormat = r.Substring(idx + 2) });
            }

            var includeDirs = filesToCopy.Where(f => autoIncludes.IsMatch(f)).Select(f => Path.GetDirectoryName(f).Replace('\\', '/')).Distinct().Select(d => "$$SYS:BSP_ROOT$$" + folderInsideBSPPrefix + (string.IsNullOrEmpty(d) ? "" : ("/" + d))).ToList();
            foreach (var f in filesToCopy)
            {
                string renamedRelativePath = f;
                string pathInsidePackage = Path.Combine(subdir, TargetFolder, f);
                if (pathInsidePackage.Length > 170)
                {
                    if (!bsp.OnFilePathTooLong(pathInsidePackage))
                        continue;
                }

                string targetFile = Path.Combine(absTarget, f);
                string newName = rules?.FirstOrDefault(r => r.Matches(f))?.Apply(targetFile);

                if (newName == null)
                    if (bsp.RenamedFileTable.TryGetValue(targetFile, out newName) || bsp.RenamedFileTable.TryGetValue(targetFile.Replace('/', '\\'), out newName))
                    {
                    }

                if (newName != null)
                {
                    var oldTargetFile = targetFile;
                    targetFile = Path.Combine(Path.GetDirectoryName(targetFile), newName);
                    renamedRelativePath = Path.Combine(Path.GetDirectoryName(renamedRelativePath), newName);
                    bsp.RenamedFileTable[oldTargetFile] = newName;
                }

                var absSourcePath = Path.Combine(expandedSourceFolder, f);

                if (AlreadyCopied)
                {
                    if (!File.Exists(targetFile))
                        throw new Exception(targetFile + " required by a copy job marked as 'Already Copied' does not exist");
                }
                else
                {
                    bool resolved = false;
                    if (potentialSymlinks.IsMatch(f))
                    {
                        for (; ; )
                        {
                            var contents = File.ReadAllLines(absSourcePath);
                            if (contents.Length == 1 && File.Exists(Path.Combine(Path.GetDirectoryName(absSourcePath), contents[0])))
                                absSourcePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(absSourcePath), contents[0]));
                            else
                                break;
                        }
                    }

                    if (!resolved)
                    {
                        File.Copy(absSourcePath, targetFile, true);
                    }
                }

                File.SetAttributes(targetFile, File.GetAttributes(targetFile) & ~FileAttributes.ReadOnly);
                string encodedPath = "$$SYS:BSP_ROOT$$" + folderInsideBSPPrefix + "/" + renamedRelativePath.Replace('\\', '/');

                if (!ExcludeFromVendorSampleMapping)
                    copiedFileMonitor?.RememberFileMapping(absSourcePath, targetFile, encodedPath);

                bool includedInProject = projectContents.IsMatch(f);

                var m = configTemplateRegex?.Match(f);
                if (m?.Success == true)
                {
                    configFiles.Add(new ConfigurationFileTemplate
                    {
                        SourcePath = encodedPath,
                        TargetFileName = Path.GetFileName(f.Substring(0, m.Groups[1].Index) + f.Substring(m.Groups[1].Index + m.Groups[1].Length)),
                    });
                }
                else if (includedInProject)
                {
                    projectFiles.Add(encodedPath.Replace('\\', '/'));

                    ParsedCondition foundCondition = null;
                    if (conditions != null)
                    {
                        foreach (var cond in conditions)
                            if (cond.Regex.IsMatch(f))
                            {
                                bsp.AddFileCondition(new FileCondition { ConditionToInclude = cond.Condition, FilePath = encodedPath });
                                cond.UseCount++;
                                cond.ReverseConditionHandle?.AttachFile(encodedPath);

                                foundCondition = cond;

                                if (!OrthogonalConditions)
                                    break;
                            }
                    }

                    bool createReverseConditionForJustFrameworkReference = foundCondition == null;
                    if (foundCondition != null && foundCondition.ReverseConditionHandle == null && ((Flags & CopyJobFlags.SimpleFileConditionsAreSecondary) != CopyJobFlags.None))
                    {
                        //This file is handled by a secondary condition (non-smart condition that depends on other variables).
                        //Vendor samples referencing this file should just reference the framework and not set any configuration parameters.
                        createReverseConditionForJustFrameworkReference = true;
                    }

                    if (createReverseConditionForJustFrameworkReference)
                        reverseConditions?.AttachFile(encodedPath);
                }
            }

            if (AdditionalProjectFiles != null)
            {
                foreach (var spec in AdditionalProjectFiles.Split(';'))
                {
                    string encodedPath = "$$SYS:BSP_ROOT$$" + folderInsideBSPPrefix + "/" + spec;
                    projectFiles.Add(encodedPath);
                }
            }

            var unusedConditions = conditions?.Where(c => c.UseCount == 0)?.ToArray();
            if (unusedConditions != null)
                foreach (var cond in unusedConditions)
                    bsp.Report.ReportMergeableError("Unused condition(s) for copy job", cond.ToString());

            if (Patches != null)
                foreach (var p in Patches)
                {
                    foreach (var fn in p.FilePath.Split(';'))
                    {
                        List<string> allLines = File.ReadAllLines(Path.Combine(absTarget, fn)).ToList();
                        p.Apply(allLines);

                        string targetPath = p.TargetPath;
                        if (targetPath == null)
                            targetPath = fn;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(absTarget, targetPath)));
                        File.WriteAllLines(Path.Combine(absTarget, targetPath), allLines);
                    }
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

                    lines.Insert(i, string.Format(GuardFormat ?? "#if defined({0}) && {0}", macro));
                    lines.Add("#endif //" + macro);
                    File.WriteAllLines(fn, lines);
                }

            if (AdditionalIncludeDirs != null)
            {
                foreach (var dir in AdditionalIncludeDirs.Split(';'))
                {
                    var mappedDir = MapIncludeDir(absTarget, subdir, dir);
                    includeDirs.Add(mappedDir);
                }
            }

            foreach (var dir in includeDirs)
            {
                reverseConditions?.AttachIncludeDir(dir);
            }

            return new ToolFlags
            {
                PreprocessorMacros = (preprocessorMacros.Count == 0) ? null : preprocessorMacros.ToArray(),
                IncludeDirectories = includeDirs.ToArray()
            };
        }

        private string MapIncludeDir(string absTarget, string subdir, string dir)
        {
            if (dir.StartsWith("$$SYS:BSP_ROOT$$/"))
                return dir;
            else
            {
                string relPath;

                if (dir == ".")
                    return ".";

                var relativeTargetFolder = TargetFolder;
                if (!string.IsNullOrEmpty(subdir))
                    relativeTargetFolder = subdir + "/" + TargetFolder;

                if (dir == "?")
                {
                    relPath = relativeTargetFolder;
                    dir = ".";
                }
                else
                    relPath = Path.Combine(relativeTargetFolder, dir).Replace('\\', '/');

                if (!dir.Contains("$$") && !Directory.Exists(Path.Combine(absTarget, dir)))
                    throw new Exception("Invalid explicit include dir: " + dir);

                if (relPath == "")
                    return "$$SYS:BSP_ROOT$$";
                else
                    return "$$SYS:BSP_ROOT$$/" + relPath.Replace('\\', '/');
            }
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
            else if ((idx = rawCond.IndexOf("!=~")) != -1)
                return new Condition.Not { Argument = new Condition.MatchesRegex { Expression = rawCond.Substring(0, idx).Trim(), Regex = rawCond.Substring(idx + 3).Trim() } };
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
        public ConfigurationFileTemplate[] ConfigurationFileTemplates;
        public string AdditionalForcedIncludes;
        public string LibraryOrder;     //A list of regexes, separated by ';', matching exactly one library each

        public ConfigFileDefinition[] ConfigFiles;
    }

    //Smart, i.e. configurable via wizard. We will eventually support 'dumb' samples just cloned from the BSP "as is".
    public class SmartSample
    {
        public string SourceFolder; //Full path (or relative to the working dir of the generator)
        public string DestinationFolder;    //Relative path under BSP
        public string[] AdditionalSources;  //Full paths ($$SYS:BSP_ROOT$$/...) of additional files that will be copied to the project dir. Can use the path/a.c=>b.c syntax to rename the file to b.c
        public string MCUFilterRegex;
        public bool IsTestProjectSample;
        public EmbeddedProjectSample EmbeddedSample;
        public string CopyFilters;
        public string[] AdditionalBuildTimeSources; //Sources that will be copied to the sample directory during BSP building as if they were present in the SourceFolder.
        public Patch[] Patches;

        public string CommonConfiguration;
    }

    public class CommonSampleConfiguration
    {
        public string BasedOn;
        public string[] RequiredFrameworks;
        public PropertyDictionary2 DefaultConfiguration;

        public static void Read(string file, Dictionary<string, string> config, HashSet<string> frameworks)
        {
            var cfg = XmlTools.LoadObject<CommonSampleConfiguration>(file);
            if (cfg.BasedOn != null)
                Read(Path.Combine(Path.GetDirectoryName(file), cfg.BasedOn), config, frameworks);

            if (cfg.RequiredFrameworks != null)
                foreach (var fw in cfg.RequiredFrameworks)
                    frameworks.Add(fw);

            if (cfg.DefaultConfiguration?.Entries != null)
                foreach (var kv in cfg.DefaultConfiguration.Entries)
                    config[kv.Key] = kv.Value;
        }
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

        public enum AutoOptionFormatType
        {
            Mask,
            Prefix
        }

        public AutoOptionFormatType AutoOptionFormat = AutoOptionFormatType.Mask;

        public class Option
        {
            public string Value;
            public string Regex;
        }

        List<KeyValuePair<Regex, string>> _Cache;

        public IEnumerable<KeyValuePair<Regex, string>> GetExpandedRules()
        {
            TryMatchMCUName("");
            return _Cache;
        }

        public string TryMatchMCUName(string name)
        {
            if (_Cache == null)
            {
                _Cache = new List<KeyValuePair<Regex, string>>();
                foreach (var op in Options ?? new Option[0])
                    _Cache.Add(new KeyValuePair<Regex, string>(new Regex(op.Regex), op.Value));
                if (AutoOptions != null)
                    foreach (var ao in AutoOptions.Split(';'))
                    {
                        string rg;
                        if (AutoOptionFormat == AutoOptionFormatType.Prefix)
                            rg = ao.TrimEnd('x') + ".*";
                        else
                            rg = ao.Replace('x', '.');

                        _Cache.Add(new KeyValuePair<Regex, string>(new Regex(rg), ao));
                    }
            }

            if (name.EndsWith("_M4"))
                name = name.Substring(0, name.Length - 3) + "xx";   //E.g. STM32MP151A_M4 => STM32MP151Axx

            foreach (var kv in _Cache)
            {
                var m = kv.Key.Match(name);
                if (m.Success)
                {
                    if (kv.Value.Contains("{"))
                        return string.Format(kv.Value, m.Groups.OfType<object>().ToArray());
                    else
                        return kv.Value;
                }
            }

            return null;
        }
    }

    public class FrameworkTemplate
    {
        public string RangeFromDir;
        public string RangeFilters;

        public string Range;
        public Framework Template;
        public string ArgumentSeparator = "\0";
        public string Separator = " ";

        //Warning: the expansion is not complete and should be updated as needed
        public IEnumerable<Framework> Expand(BSPBuilder bspBuilder)
        {
            XmlSerializer ser = new XmlSerializer(typeof(Framework));
            MemoryStream ms = new MemoryStream();
            ser.Serialize(ms, Template);

            if (Separator == "\\n")
                Separator = "\n";

            string[] range;

            if (!string.IsNullOrEmpty(Range))
                range = Range.Split(Separator[0]);
            else
            {
                if (string.IsNullOrEmpty(RangeFromDir))
                    throw new Exception($"Framework template for {Template?.Name} does not specify neither range nor range dir");

                var dir = bspBuilder.ExpandVariables(RangeFromDir);
                if (!Directory.Exists(dir))
                    throw new DirectoryNotFoundException("Missing " + dir);

                var filters = new CopyFilters(RangeFilters);
                range = Directory.GetDirectories(dir).Select(Path.GetFileName).Where(filters.IsMatch).ToArray();
            }

            foreach (var item in range)
            {
                var n = item.Trim(' ', '\r', '\n', '\t');
                if (n == "")
                    continue;

                ms.Seek(0, SeekOrigin.Begin);
                Framework deepCopy = (Framework)ser.Deserialize(ms);
                Expand(ref deepCopy.Name, n);
                Expand(ref deepCopy.ID, n);
                Expand(ref deepCopy.ClassID, n);
                Expand(ref deepCopy.ProjectFolderName, n);
                foreach (var job in deepCopy.CopyJobs)
                {
                    Expand(ref job.SourceFolder, n);
                    Expand(ref job.TargetFolder, n);
                    Expand(ref job.FilesToCopy, n);
                    Expand(ref job.ProjectInclusionMask, n);
                    Expand(ref job.SmartPropertyGroup, n);
                }

                if (deepCopy.ConfigurableProperties?.PropertyGroups != null)
                {
                    foreach(var g in deepCopy.ConfigurableProperties.PropertyGroups)
                    {
                        Expand(ref g.Name, n);
                        Expand(ref g.UniqueID, n);
                    }
                }

                yield return deepCopy;
            }
        }

        void Expand(ref string str, string name)
        {
            if (str == null)
                return;
            if (ArgumentSeparator[0] == '\0')
                str = str.Replace("$$BSPGEN:FRAMEWORK$$", name).Replace("$$BSPGEN:FRAMEWORK_LOWER$$", name.ToLower());
            else
            {
                string[] args = name.Split(ArgumentSeparator[0]);
                for (int i = 0; i < args.Length; i++)
                {
                    if (i == 0)
                        str = str.Replace("$$BSPGEN:FRAMEWORK$$", args[i]);
                    else
                        str = str.Replace($"$$BSPGEN:FRAMEWORKARG{i}$$", args[i]);
                }
            }
        }
    }

    //This structure defines a configuration file parameter that can conditionally enable various declarations (e.g. HAL_xxx_MODULE_ENABLED).
    //The BSP generation logic can automatically determine the declarations enabled by it and insert hints for them into the BSP.
    public struct TestableConfigurationFileParameter
    {
        public string Name;
        public string DisabledValue, EnabledValue;

        public override string ToString() => Name;
    }

    public class ConfigurationFileTemplateEx
    {
        public ConfigurationFileTemplate Template;

        public TestableConfigurationFileParameter[] TestableParameters;
        public string[] TestableHeaderFiles;

        public ConfigurationFileTemplateEx(ConfigurationFileTemplate template)
        {
            Template = template;
        }
    }

    public interface IConfigurationFileParser
    {
        ConfigurationFileTemplateEx BuildConfigurationFileTemplate(string file, ConfigFileDefinition cf);
    }

    public class ConfigFileDefinition
    {
        public string Path;
        public string ParserClass;
        public bool SeparateConfigsForEachMCU;
        public string FinalName;

        public string TargetPathForInsertingIntoProject;
        public string[] TestableHeaderFiles;

        public ConfigFileDefinition ShallowClone() => (ConfigFileDefinition)MemberwiseClone();
    }

    public class FamilyDefinition
    {
        public string Name;
        public string DeviceRegex;
        public string FamilySubdirectory;
        public string PrimaryHeaderDir;
        public string StartupFileDir;

        public bool HasMixedCores; //Different devices within the family can have different cores (e.g. Cortex-M0 vs Cortex-M4)
        public bool HasMixedFPUs;

        public Framework CoreFramework;

        public Framework[] AdditionalFrameworks;
        public SmartSample[] SmartSamples;
        public MCUClassifier[] Subfamilies;
        public ToolFlags CompilationFlags = new ToolFlags();
        public SysVarEntry[] AdditionalSystemVars;
        public PropertyList ConfigurableProperties;
        public ConditionalToolFlags[] ConditionalFlags;
        public FrameworkTemplate[] AdditionalFrameworkTemplates;
        public CodeInsertionPoint[] InitializationCodeInsertionPoints;
        public string[] AdditionalSourceFiles;

        public ConfigFileDefinition[] ConfigFiles;
    }
}
