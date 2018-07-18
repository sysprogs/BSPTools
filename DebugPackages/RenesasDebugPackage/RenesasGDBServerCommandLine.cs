using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RenesasDebugPackage
{
    class RenesasGDBServerCommandLine
    {
        List<string> _SplitCommandLine;

        public RenesasGDBServerCommandLine(string commandLine)
        {
            CommandLine = commandLine;
        }

        public string GetSeparatedValue(string key)
        {
            for (int i = 0; i < _SplitCommandLine.Count - 1; i++)
                if (_SplitCommandLine[i] == key)
                    return _SplitCommandLine[i + 1];
            return null;
        }

        public void SetSeparatedValue(string key, string value)
        {
            for (int i = 0; i < _SplitCommandLine.Count - 1; i++)
                if (_SplitCommandLine[i] == key)
                {
                    if (value == null)
                        _SplitCommandLine.RemoveRange(i, 2);
                    else
                        _SplitCommandLine[i + 1] = value;
                    return;
                }

            if (value != null)
            {
                _SplitCommandLine.Insert(0, key);
                _SplitCommandLine.Insert(1, value);
            }
        }

        public string DebugInterface
        {
            get => GetSeparatedValue("-g");
            set => SetSeparatedValue("-g", value);
        }

        public string DeviceID
        {
            get => GetSeparatedValue("-t");
            set => SetSeparatedValue("-t", value);
        }

        static int ParseInt(string str)
        {
            if (int.TryParse(str, out var tmp))
                return tmp;
            else
                return 0;
        }

        public int GDBPort
        {
            get => ParseInt(GetSeparatedValue("-p"));
            set => SetSeparatedValue("-p", value.ToString());
        }

        public int AuxiliaryPort
        {
            get => ParseInt(GetSeparatedValue("-d"));
            set => SetSeparatedValue("-d", value.ToString());
        }

        public string CommandLine
        {
            get => string.Join(" ", _SplitCommandLine.ToArray());
            set => _SplitCommandLine = (value ?? "").Split(' ').ToList();
        }

        public string TryGetValue(string key)
        {
            string prefix = $"-{key}=";
            return _SplitCommandLine.FirstOrDefault(s => s.StartsWith(prefix))?.Substring(prefix.Length);
        }

        public void SetValue(string key, string value)
        {
            string prefix = $"-{key}=";
            for (int i = 0; i < _SplitCommandLine.Count; i++)
            {
                if (_SplitCommandLine[i].StartsWith(prefix))
                {
                    if (value == null)
                        _SplitCommandLine.RemoveAt(i);
                    else
                        _SplitCommandLine[i] = prefix + value;
                    return;
                }
            }

            if (value != null)
                _SplitCommandLine.Add(prefix + value);
        }
    }
}
