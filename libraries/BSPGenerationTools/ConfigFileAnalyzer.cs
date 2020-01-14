using BSPEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BSPGenerationTools
{
    class ConfigFileAnalyzer : IDisposable
    {
        private string _Clang, _Dir, _CFile, _RSPFile;
        private readonly string _ConfigFile;
        private readonly ConfigurationFileTemplate _Template;
        Dictionary<string, string> _CurrentConfig = new Dictionary<string, string>();

        public ConfigFileAnalyzer(string clang, string dir, string cFile, string rspFile, string configFile, ConfigurationFileTemplateEx template)
        {
            _Clang = clang;
            _Dir = dir;
            _CFile = cFile;
            _RSPFile = rspFile;
            _ConfigFile = configFile;
            _Template = template.Template;

            foreach(var p in template.TestableParameters)
                _CurrentConfig[p.Name] = p.EnabledValue;

            ApplyCurrentConfig();
        }

        void ApplyCurrentConfig()
        {
            var lines = File.ReadAllLines(_ConfigFile);

            var entries = ConfigurationFileEditor.ParseConfigurationFile(lines, _Template, null);
            var edits = ConfigurationFileEditor.ComputeNecessaryEdits(entries, _CurrentConfig);

            foreach (var e in edits)
                lines[e.LineIndex] = e.NewValue;

            File.WriteAllLines(_ConfigFile, lines);
        }

        public void Dispose()
        {
        }

        public string[] BuildGlobalSymbolList()
        {
            var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{_Clang}\" -cc1 -fsyntax-only -ast-dump @{Path.GetFileName(_RSPFile)} {Path.GetFileName(_CFile)} > ast.txt 2>&1") { UseShellExecute = false, WorkingDirectory = _Dir });
            proc.WaitForExit();

            Regex rgGlobalSymbol = new Regex(@"\|-(FunctionDecl|RecordDecl|TypedefDecl) 0x[0-9a-f]+ (<[^<>]+>|<<[^<>]+>>|[^<> ]+) (<[^<>]+>|<<[^<>]+>>|[^<> ]+)(| referenced) ([^ ]+) '.*");
            List<string> result = new List<string>();
            foreach (var line in File.ReadAllLines(Path.Combine(_Dir, "ast.txt")))
            {
                var m = rgGlobalSymbol.Match(line);
                if (m.Success)
                {
                    result.Add(m.Groups[5].Value);
                }
            }

            return result.ToArray();
        }

        public void SetParameterValue(string name, string value, bool flush)
        {
            _CurrentConfig[name] = value;
            ApplyCurrentConfig();
        }
    }
}
