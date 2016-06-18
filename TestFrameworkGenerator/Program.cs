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
        }

        static string MakeRelativePath(string path)
        {
            return path.Replace("$$SYS:BSP_ROOT$$/", "");
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

            fwObj.Common.AdditionalSourceFiles = unconditionalFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).Select(f=> MakeRelativePath(f)).ToArray();
            fwObj.Common.AdditionalHeaderFiles = unconditionalFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).Select(f => MakeRelativePath(f)).ToArray();

            fwObj.Common.AdditionalIncludeDirs = flags.IncludeDirectories?.Select(f => MakeRelativePath(f))?.ToArray();
            fwObj.Common.AdditionalPreprocessorMacros = flags.PreprocessorMacros?.Select(f => MakeRelativePath(f))?.ToArray();

            if (fwObj.Embedded != null)
            {
                fwObj.Embedded.AdditionalSourceFiles = embeddedFiles.Where(f => !MCUFamilyBuilder.IsHeaderFile(f)).Select(f => MakeRelativePath(f)).ToArray();
                fwObj.Embedded.AdditionalHeaderFiles = embeddedFiles.Where(f => MCUFamilyBuilder.IsHeaderFile(f)).Select(f => MakeRelativePath(f)).ToArray();
            }

            XmlTools.SaveObject(fwObj, Path.Combine(dummyBSPBuilder.Directories.OutputDir, "TestFramework.xml"));

            string outDir = dummyBSPBuilder.Directories.OutputDir;
            string archiveName = $"{fwObj.ID.Split('.').Last()}-{fwObj.Version}.vgdbxtfp";
            Console.WriteLine("Building archive...");
            TarPacker.PackDirectoryToTGZ(outDir, Path.Combine(dummyBSPBuilder.Directories.OutputDir, archiveName), fn => Path.GetExtension(fn).ToLower() != ".vgdbxtfp");
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
