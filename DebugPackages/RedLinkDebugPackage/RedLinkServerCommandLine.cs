using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RedLinkDebugPackage
{
    class RedLinkServerCommandLine
    {
        List<string> _SplitCommandLine;
        
        public const string DefaultVendor = "$$REDLINK:VENDOR_ID$$";
        public const string DefaultDevice = "$$REDLINK:DEVICE_ID$$";
        public const string DefaultCore = "$$REDLINK:CORE_INDEX$$";

        public RedLinkServerCommandLine(string commandLine)
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

        public string CommandLine
        {
            get => string.Join(" ", _SplitCommandLine.ToArray());
            set => _SplitCommandLine = (value ?? "").Split(' ').ToList();
        }

        public string TryGetPrefixedValue(string prefix)
        {
            return _SplitCommandLine.FirstOrDefault(s => s.StartsWith(prefix))?.Substring(prefix.Length);
        }

        public void SetPrefixedValue(string prefix, string value)
        {
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

        public string Device
        {
            get => TryGetPrefixedValue("-p");
            set => SetPrefixedValue("-p", value);
        }

        public string Vendor
        {
            get => TryGetPrefixedValue("-vendor=");
            set => SetPrefixedValue("-vendor=", value);
        }

        public string Core
        {
            get => TryGetPrefixedValue("-CoreIndex=");
            set => SetPrefixedValue("-CoreIndex=", value);
        }
    }
}
