using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using VisualGDB.TestEngine;
using BSPGenerationTools;
using LinkerScriptGenerator;

namespace CppUTest
{
    class Program
    {
        class DummyBSPBuilder : BSPBuilder
        {
            public string FrameworkID;

            public DummyBSPBuilder(BSPDirectories dirs) : base(dirs, null)
            {
            }

            public override void GetMemoryBases(out uint flashBase, out uint ramBase)
            {
                throw new NotImplementedException();
            }

            public override MemoryLayout GetMemoryLayout(MCUBuilder mcu, MCUFamilyBuilder family)
            {
                throw new NotImplementedException();
            }

            public string MakeRelativePath(string path)
            {
                if (!path.Contains("$$") && !Path.IsPathRooted(path))
                    path = $"$$SYS:TESTFW_BASE$$/{FrameworkID}/" + path.Replace('\\', '/');
                return path.Replace("$$SYS:BSP_ROOT$$/", $"$$SYS:TESTFW_BASE$$/{FrameworkID}/");
            }

        }


        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            if (args.Length > 2)
                throw new Exception("Usage: TestFrameworkGenerator <Rules subdir> <Source directory>");
            var dummyBSPBuilder = new DummyBSPBuilder(new BSPDirectories(args[1], @"..\..\Output\" + args[0], @"..\..\rules\" + args[0]));
            if (Directory.Exists(dummyBSPBuilder.Directories.OutputDir))
                Directory.Delete(dummyBSPBuilder.Directories.OutputDir, true);
            Directory.CreateDirectory(dummyBSPBuilder.Directories.OutputDir);


            var fwObj = XmlTools.LoadObject<TestFrameworkDefinition>(Path.Combine(dummyBSPBuilder.Directories.RulesDir, "TestFramework.xml"));
            dummyBSPBuilder.FrameworkID = fwObj.ID;
            var rules = XmlTools.LoadObject<TestFrameworkRules>(Path.Combine(dummyBSPBuilder.Directories.RulesDir, "rules.xml"));
            List<string> projectFiles = new List<string>();
            ToolFlags flags = new ToolFlags();
            foreach(var job in rules.CopyJobs)
            {
                flags = flags.Merge(job.CopyAndBuildFlags(dummyBSPBuilder, projectFiles, null));
            }

            Dictionary<string, FileCondition> matchedConditions = new Dictionary<string, FileCondition>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var cond in dummyBSPBuilder.MatchedFileConditions)
                matchedConditions[cond.FilePath] = cond;

            var unconditionalFiles = projectFiles.Where(f => !matchedConditions.ContainsKey(f));
            List<string> embeddedFiles = new List<string>();
            foreach(var f in projectFiles)
            {
                FileCondition cond;
                if (matchedConditions.TryGetValue(f, out cond))
                {
                    var eq = (cond.ConditionToInclude as Condition.Equals);
                    if (eq?.Expression != "$$platform$$")
                        throw new Exception("Unexpected condition for " + f);
                    switch(eq.ExpectedValue)
                    {
                        case "embedded":
                            embeddedFiles.Add(f);
                            break;
                        default:
                            throw new Exception("Invalid platform condition");
                    }
                }
            }

            if (fwObj.Common == null)
                fwObj.Common = new TestFrameworkDefinition.TestPlatformBuild();

            fwObj.Common.AdditionalSourceFiles = unconditionalFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).Select(f=> dummyBSPBuilder.MakeRelativePath(f)).ToArray();
            fwObj.Common.AdditionalHeaderFiles = unconditionalFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).Select(f => dummyBSPBuilder.MakeRelativePath(f)).ToArray();

            fwObj.Common.AdditionalIncludeDirs = flags.IncludeDirectories?.Select(f => dummyBSPBuilder.MakeRelativePath(f))?.ToArray();
            fwObj.Common.AdditionalPreprocessorMacros = flags.PreprocessorMacros?.Select(f => dummyBSPBuilder.MakeRelativePath(f))?.ToArray();

            if (fwObj.Embedded != null)
            {
                fwObj.Embedded.AdditionalSourceFiles = embeddedFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).Select(f => dummyBSPBuilder.MakeRelativePath(f)).ToArray();
                fwObj.Embedded.AdditionalHeaderFiles = embeddedFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).Select(f => dummyBSPBuilder.MakeRelativePath(f)).ToArray();
                fwObj.Embedded.AdditionalIncludeDirs = fwObj.Embedded.AdditionalIncludeDirs?.Select(f => dummyBSPBuilder.MakeRelativePath(f))?.ToArray();
            }

            CopyAndAdjustSamples(dummyBSPBuilder, fwObj.Embedded.Samples);
            CopyAndAdjustSamples(dummyBSPBuilder, fwObj.Common.Samples);

            XmlTools.SaveObject(fwObj, Path.Combine(dummyBSPBuilder.Directories.OutputDir, "TestFramework.xml"));

            string outDir = dummyBSPBuilder.Directories.OutputDir;
            string archiveName = $"{fwObj.ID.Split('.').Last()}-{fwObj.Version}.vgdbxtfp";
            Console.WriteLine("Building archive...");
            TarPacker.PackDirectoryToTGZ(outDir, Path.Combine(dummyBSPBuilder.Directories.OutputDir, archiveName), fn => Path.GetExtension(fn).ToLower() != ".vgdbxtfp");
        }

        private static void CopyAndAdjustSamples(DummyBSPBuilder builder, TestFrameworkDefinition.TestFrameworkSample[] samples)
        {
            if (samples == null)
                return;
            foreach(var sample in samples)
            {
                for (int i = 0; i < sample.Files.Length; i++)
                {
                    string targetPath = Path.Combine(builder.Directories.OutputDir, sample.Files[i]);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                    File.Copy(Path.Combine(builder.Directories.RulesDir, sample.Files[i]), targetPath);
                    sample.Files[i] = $"$$SYS:TESTFW_BASE$$/{builder.FrameworkID}/" + sample.Files[i].Replace('\\', '/');
                }
            }
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (args.Name.StartsWith("BSPEngine,"))
                return typeof(EmbeddedFramework).Assembly;
            return null;
        }
    }

    public class TestFrameworkRules
    {
        public CopyJob[] CopyJobs;
    }
}
