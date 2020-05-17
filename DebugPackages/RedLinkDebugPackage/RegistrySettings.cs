using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RedLinkDebugPackage
{
    class RegistrySettings
    {
        static RegistryKey _Key;

        static RegistryKey Key
        {
            get
            {
                if (_Key == null)
                    _Key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Sysprogs\DebugPackages\RedLink");

                return _Key;
            }
        }

        public static string MCUXpressoPath
        {
            get => Key.GetValue(nameof(MCUXpressoPath)) as string;
            set => Key.SetValue(nameof(MCUXpressoPath), value ?? "");
        }
    }
}
