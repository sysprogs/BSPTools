using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using UsbDriverTool;

namespace AVaRICEDebugPackage
{
    class UsbDriverHelper
    {
        public struct UsbIdentity
        {
            public string VID;
            public string PID;
            public string Interface;

            public override string ToString()
            {
                if (string.IsNullOrEmpty(Interface))
                    return VID + ":" + PID;
                else
                    return VID + ":" + PID + ":" + Interface;
            }
        }

        public static DeviceRecord TryFindDeviceInstance(UsbIdentity[] usbIdentities)
        {
            if (usbIdentities == null)
                throw new ArgumentNullException("usbIdentities");

            var allDevices = DeviceRecord.FindAllUsbDevices();
            if (allDevices == null)
                return null;

            foreach (var dev in allDevices)
            {
                foreach (var id in usbIdentities)
                    if (string.Equals(dev.VendorID, id.VID, StringComparison.InvariantCultureIgnoreCase) && string.Equals(dev.ProductID, id.PID, StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(id.Interface) || string.Equals(id.Interface, dev.Interface))
                            return dev;
                    }
            }

            return null;
        }

        public static bool TryCheckDeviceAndInstallDriversInteractively(UsbIdentity[] usbIdentities, string universalDriverId)
        {
            DeviceRecord dev;

            for (;;)
            {
                dev = TryFindDeviceInstance(usbIdentities);
                if (dev != null)
                    break;

                string msg;
                if (usbIdentities.Length == 1)
                    msg = string.Format("Cannot find a USB device with the following VID/PID: {0}. Please check that your JTAG/SWD debugger is connected.", usbIdentities[0]);
                else
                {
                    StringBuilder msgBuilder = new StringBuilder("Cannot find a USB device with one of the following VID/PIDs:\r\n");
                    foreach (var id in usbIdentities)
                        msgBuilder.AppendLine("    " + id.ToString());

                    msgBuilder.Append("Please check that your JTAG/SWD debugger is connected.");
                    msg = msgBuilder.ToString();
                }

                switch (MessageBox.Show(msg, "VisualGDB", MessageBoxButtons.AbortRetryIgnore, MessageBoxIcon.Warning))
                {
                    case DialogResult.Retry:
                        continue;
                    case DialogResult.Ignore:
                        return true;
                    case DialogResult.Abort:
                    default:
                        return false;
                }
            }

            if (!string.IsNullOrEmpty(universalDriverId))
            {
                List<UniversalUsbDriver> universalDrivers = new List<UniversalUsbDriver>();
                try
                {
                    UniversalUsbDriver.LoadFromDriversSubdirectory(universalDrivers);
                }
                catch { }

                UniversalUsbDriver requiredDriver = null;

                foreach (var drv in universalDrivers)
                    if (drv.UniqueDriverId == universalDriverId)
                    {
                        requiredDriver = drv;
                        break;
                    }

                if (requiredDriver == null || string.IsNullOrEmpty(requiredDriver.UniversalDriverName))
                    return MessageBox.Show("Warning: cannot find a universal driver " + universalDriverId + ". Continue anyway?", "VisualGDB", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

                bool driverMissing;
                if (requiredDriver.FixedDriver != null && requiredDriver.FixedDriver.DeviceNameRegex != null)
                {
                    try
                    {
                        var rg = new Regex(requiredDriver.FixedDriver.DeviceNameRegex);
                        driverMissing = dev.Device.UserFriendlyName != null && !rg.IsMatch(dev.Device.UserFriendlyName);
                    }
                    catch
                    {
                        driverMissing = false;
                    }
                }
                else
                    driverMissing = dev.Device.UserFriendlyName != null && !dev.Device.UserFriendlyName.EndsWith("(" + requiredDriver.UniversalDriverName + ")");

                if (driverMissing)
                    if (MessageBox.Show(string.Format("\"{0}\" does not appear to have \"{1}\" driver installed. AVaRICE may have problems finding your device. Try installing it now?", dev.Device.UserFriendlyName, requiredDriver.UniversalDriverName), "VisualGDB", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        UsbDriverInstallProgressForm.DoInstallDriverWithProgress(dev, requiredDriver);

                return true;
            }

            return true;
        }
    }
}
