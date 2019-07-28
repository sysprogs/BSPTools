using OpenOCDPackage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using BSPEngine;

namespace RISCVDebugPackage
{
    [XmlType("com.visualgdb.edp.openocd.settings.risc-v")]
    public class RISCVOpenOCDSettings : OpenOCDSettings
    {
        public RISCVResetMode ResetMode { get; set; } = RISCVResetMode.nSRST;
    }

    public enum RISCVResetMode
    {
        Manual,
        nSRST,
    }

    public class RISCVOpenOCDSettingsEditor : OpenOCDSettingsEditor
    {
        public new RISCVOpenOCDSettings Settings => (RISCVOpenOCDSettings)base.Settings;

        public RISCVOpenOCDSettingsEditor(IBSPConfiguratorHost host, string baseDir, RISCVOpenOCDSettings settings, KnownInterfaceInstance context)
            : base(host, baseDir, settings ?? new RISCVOpenOCDSettings(), context, settings == null)
        {
        }

        public System.Windows.Visibility ProgramOptionVisibility => System.Windows.Visibility.Collapsed;

        protected override void InsertResetAndHaltCommands(int idxLoad, QuickSetupDatabase.ProgrammingInterface iface, QuickSetupDatabase.TargetDeviceFamily device)
        {
            const string unprotectCommand = "mon flash protect 0 64 last off";
            const string resetHalt = "mon reset halt";
            const string restartAtEntry = "mon reset halt"; // set $pc = _start";
            int idx;

            idx = Settings.StartupCommands.IndexOf(unprotectCommand);
            if (idx == -1)
                Settings.StartupCommands.Insert(idxLoad, unprotectCommand);

            idx = Settings.StartupCommands.IndexOf(resetHalt);
            if (idx == -1)
                Settings.StartupCommands.Insert(idxLoad, resetHalt);

            idx = Settings.StartupCommands.IndexOf(restartAtEntry, idxLoad + 1);
            if (idx == -1)
                Settings.StartupCommands.Add(restartAtEntry);
        }

        public static bool IsE300CPU(LoadedBSP.ConfiguredMCU mcu)
        {
            string tmp = null;
            if (mcu.Configuration?.TryGetValue("com.sysprogs.risc-v.board", out tmp) == true && tmp.Contains("-e300-"))
                return true;
            return false;
        }

        protected override QuickSetupDatabase.TargetDeviceFamily TryDetectDeviceById(QuickSetupDatabase db, LoadedBSP.ConfiguredMCU mcu)
        {
            if (IsE300CPU(mcu))
                return db.TryDetectDeviceById(mcu, "E300");
            else
                return base.TryDetectDeviceById(db, mcu);
        }
    }
}
