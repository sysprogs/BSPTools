using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace AVaRICEDebugPackage
{
    public partial class UsbDriverInstallProgressForm : Form
    {
        Process _Process;
        UsbDriverInstallProgressForm(Process process, string deviceName, string driverName)
        {
            InitializeComponent();
            _Process = process;
            lblDriver.Text = driverName;
            lblDevice.Text = deviceName;
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (_Process.HasExited)
                Close();
        }

        internal static void DoInstallDriverWithProgress(UsbDriverTool.DeviceRecord dev, UsbDriverTool.UniversalUsbDriver requiredDriver)
        {
            try
            {
                var process = Process.Start(AVaRICEDebugExtension.UsbDriverToolExe, "/autoinstall \"" + dev.Device.DeviceID + "\" \"" + requiredDriver.UniqueDriverId + "\"");
                new UsbDriverInstallProgressForm(process, dev.Device.UserFriendlyName, requiredDriver.UniversalDriverName).ShowDialog();
                process.WaitForExit();

                if (process.ExitCode != 0)
                    throw new Exception("USB driver installation failed with code " + process.ExitCode);
            }
            catch(Exception ex)
            {
                MessageBox.Show(ex.Message, "VisualGDB", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

        }
    }
}
