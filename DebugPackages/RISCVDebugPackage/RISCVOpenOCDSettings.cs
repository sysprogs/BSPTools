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
    }

    public class RISCVOpenOCDSettingsEditor : OpenOCDSettingsEditor
    {
        public new RISCVOpenOCDSettings Settings => (RISCVOpenOCDSettings)base.Settings;

        public RISCVOpenOCDSettingsEditor(IBSPConfiguratorHost host, string baseDir, RISCVOpenOCDSettings settings, KnownInterfaceInstance context)
            : base(host, baseDir, settings ?? new RISCVOpenOCDSettings(), context, settings == null)
        {
        }

        protected override void InsertResetAndHaltCommands(int idxLoad, QuickSetupDatabase.ProgrammingInterface iface, QuickSetupDatabase.TargetDeviceFamily device)
        {
            const string unprotectCommand = "mon flash protect 0 64 last off";
            const string resetHalt = "mon reset halt";
            const string restartAtEntry = "set $pc = _start";
            int idx;

            idx = Settings.StartupCommands.IndexOf(unprotectCommand);
            if (idx == -1)
                Settings.StartupCommands.Insert(idxLoad, unprotectCommand);

            idx = Settings.StartupCommands.IndexOf(resetHalt);
            if (idx == -1)
                Settings.StartupCommands.Insert(idxLoad, resetHalt);

            idx = Settings.StartupCommands.IndexOf(restartAtEntry);
            if (idx == -1)
                Settings.StartupCommands.Add(restartAtEntry);
        }

        protected override QuickSetupDatabase.TargetDeviceFamily TryDetectDeviceById(QuickSetupDatabase db, LoadedBSP.ConfiguredMCU mcu)
        {
            string tmp = null;
            if (mcu.Configuration?.TryGetValue("com.sysprogs.risc-v.board", out tmp) == true && tmp.Contains("-e300-"))
                return db.TryDetectDeviceById(mcu, "E300");
            else
                return base.TryDetectDeviceById(db, mcu);
        }
    }
}
