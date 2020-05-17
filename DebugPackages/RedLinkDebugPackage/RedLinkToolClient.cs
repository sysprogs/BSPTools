using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RedLinkDebugPackage
{
    class RedLinkToolClient : IDisposable
    {
        Process _Process;
        object _CommandLock = new object();

        ManualResetEvent _Initialized = new ManualResetEvent(false);
        bool _Shutdown;
        private Thread _OutputReadingThread;

        public RedLinkToolClient()
        {
            var toolPath = Path.Combine(RegistrySettings.MCUXpressoPath, @"binaries\rltool.exe");
            if (!File.Exists(toolPath))
                throw new Exception("Missing " + toolPath);

            _Process = new Process();
            _Process.StartInfo.FileName = toolPath;
            _Process.StartInfo.UseShellExecute = false;
            _Process.StartInfo.RedirectStandardInput = true;
            _Process.StartInfo.RedirectStandardOutput = true;
            _Process.StartInfo.RedirectStandardError = true;
            _Process.StartInfo.CreateNoWindow = true;

            _Process.ErrorDataReceived += _Process_OutputDataReceived;
            _Process.Start();

            _Process.BeginErrorReadLine();
            _OutputReadingThread = new Thread(OutputReadingThreadBody);
            _OutputReadingThread.Start();

            if (!_Initialized.WaitOne(5000))
            {
                Dispose();
                throw new Exception("rltool.exe failed to initialize");
            }
        }

        ManualResetEvent _CommandDone = new ManualResetEvent(false);
        string _LastCommandOutput;

        void OutputReadingThreadBody()
        {
#if DEBUG
            Thread.CurrentThread.Name = "RedLinkTool output reading thread";
#endif
            char[] buffer = new char[1024];

            try
            {
                string accumulatedText = "";
                bool initialized = false;
                while (!_Shutdown)
                {
                    int done = _Process.StandardOutput.Read(buffer, 0, buffer.Length);
                    if (done <= 0)
                        break;

                    accumulatedText += new string(buffer, 0, done);
                    if (accumulatedText.EndsWith("\n> "))    //End of command output
                    {
                        if (!initialized)
                        {
                            initialized = true;
                            _Initialized.Set();
                        }
                        else
                        {
                            _LastCommandOutput = accumulatedText;
                            _CommandDone.Set();
                        }

                        accumulatedText = "";
                    }
                }
            }
            catch
            {
                return;
            }
        }

        public EventHandler<DataReceivedEventArgs> StderrLineReceived;

        private void _Process_OutputDataReceived(object sender, DataReceivedEventArgs e) => StderrLineReceived?.Invoke(sender, e);

        public void Dispose()
        {
            if (_Process.HasExited)
                return;

            try
            {
                RunCommand("exit", _OutputReadingThread.IsAlive ? 500 : 0, false);
            }
            catch { }

            _Shutdown = true;
            if (!_Process.WaitForExit(500))
                _Process.Kill();
        }

        public string[] RunCommand(string command, int timeout = 1000, bool throwOnFailure = true)
        {
            if (_Process == null)
                throw new Exception("The RedLink live memory engine has not been started yet");

            lock (_CommandLock)
            {
                _CommandDone.Reset();
                _Process.StandardInput.WriteLine(command);
                if (_CommandDone.WaitOne(timeout))
                    return (Interlocked.Exchange(ref _LastCommandOutput, null) ?? "").Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

                if (throwOnFailure)
                    throw new TimeoutException($"Timed out running the '{command}'");

                return null;
            }
        }

        public struct ProbeInfo
        {
            public int Index;
            public string Manufacturer, Description, SerialNumber;

            public override string ToString() => Description;
        }

        public ProbeInfo[] GetAllProbes()
        {
            List<ProbeInfo> probes = new List<ProbeInfo>();

            ProbeInfo constructedProbe = default;
            var output = RunCommand("srv PROBELIST");
            foreach (var line in output)
            {
                if (line.Trim() == "")
                {
                    if (constructedProbe.Index != 0 && constructedProbe.SerialNumber != null)
                        probes.Add(constructedProbe);
                    constructedProbe = default;
                }

                int idx = line.IndexOf('=');
                if (idx != -1)
                {
                    string key = line.Substring(0, idx).Trim();
                    string value = line.Substring(idx + 1).Trim();

                    switch(key)
                    {
                        case "Index":
                            int.TryParse(value, out constructedProbe.Index);
                            break;
                        case "Manufacturer":
                            constructedProbe.Manufacturer = value;
                            break;
                        case "Description":
                            constructedProbe.Description = value;
                            break;
                        case "Serial Number":
                            constructedProbe.SerialNumber = value;
                            break;
                    }
                }
            }

            if (constructedProbe.Index != 0 && constructedProbe.SerialNumber != null)
                probes.Add(constructedProbe);

            return probes.ToArray();
        }
    }
}
