using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RedLinkDebugPackage
{
    class RedLinkLiveMemoryEvaluator : ILiveMemoryEvaluator2
    {
        private string _SerialNumber;
        int _ProbeIndex, _CoreIndex;

        RedLinkToolClient _Tool;

        public RedLinkLiveMemoryEvaluator(string explicitSerialNumber, int coreIndex)
        {
            _SerialNumber = explicitSerialNumber;
            _CoreIndex = coreIndex;
        }

        public int[] SupportedAccessSizes { get; } = new[] { -1, 1, 2, 4 };

        public event LiveMemoryLineHandler LineTransferred;

        public void Dispose()
        {
            _Tool?.Dispose();
        }

        public byte[] ReadMemory(ulong address, int size)
        {
            AlignedIORequest[] requests = new[] { new AlignedIORequest { Address = address, WordSize = 1, WordCount = (uint)size, Buffer = new byte[size], Direction = LiveMemoryIODirection.Read } };
            if (!RunMultipleIORequests(requests))
                throw new Exception($"Failed to read memory at {address:x8}");

            if (requests[0].BytesDone != (uint)size)
                throw new Exception($"Failed to read memory at {address:x8}");

            return requests[0].Buffer;
        }

        public bool RunMultipleIORequests(AlignedIORequest[] requests)
        {
            StringBuilder requestBuilder = new StringBuilder();
            string requestNumberPrefix = "__CurrentRequest=";
            List<string> temporaryFiles = new List<string>();

            try
            {
                for (int i = 0; i < requests.Length; i++)
                {
                    requestBuilder.Append($"echo {requestNumberPrefix}{i}; ");
                    if (requests[i].Direction == LiveMemoryIODirection.Write)
                    {
                        if (requests[i].WordCount == 1 && (requests[i].WordSize == 1 || requests[i].WordSize == 2 || requests[i].WordSize == 4))
                            requestBuilder.Append($"srv POKE{requests[i].WordSize * 8} {_ProbeIndex} {_CoreIndex} 0x{requests[i].Address:x8} {FormatByteValueAsSingleNumber(requests[i])};");
                        else
                        {
                            var tmpFile = Path.GetTempFileName();
                            temporaryFiles.Add(tmpFile);
                            File.WriteAllBytes(tmpFile, requests[i].Buffer.Skip(requests[i].BufferOffset).Take(requests[i].ByteCount).ToArray());
                            tmpFile = tmpFile.Replace('\\', '/');
                            requestBuilder.Append($"srv MEMLOAD {_ProbeIndex} {_CoreIndex} \"{tmpFile}\" 0x{requests[i].Address:x8} {requests[i].ByteCount};");
                        }
                    }
                    else
                        requestBuilder.Append($"srv MEMDUMP {_ProbeIndex} {_CoreIndex} 0x{requests[i].Address:x8} {requests[i].ByteCount};");
                }

                var output = _Tool.RunCommand(requestBuilder.ToString(), 5000);

                int currentIndex = -1;
                int firstLine = -1;

                for (int i = 0; i < output.Length; i++)
                {
                    var line = output[i];
                    bool isNewRequest = line.StartsWith(requestNumberPrefix);

                    if (isNewRequest || line == "> ")
                    {
                        if (firstLine != -1 && currentIndex >= 0 && currentIndex < requests.Length)
                            ProcessRequestOutput(ref requests[currentIndex], output, firstLine, i - firstLine);

                        if (!isNewRequest || !int.TryParse(line.Substring(requestNumberPrefix.Length), out currentIndex))
                            currentIndex = -1;

                        firstLine = i + 1;
                    }
                }
            }
            finally
            {
                foreach(var tmp in temporaryFiles)
                {
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch { }
                }
            }

            return true;
        }

        private string FormatByteValueAsSingleNumber(AlignedIORequest rq)
        {
            StringBuilder result = new StringBuilder();
            result.Append("0x");
            for (int i = 0; i < rq.ByteCount; i++)
                result.Append($"{rq.Buffer[rq.BufferOffset + rq.ByteCount - i - 1]:x2}");
            return result.ToString();
        }

        private void ProcessRequestOutput(ref AlignedIORequest request, string[] output, int firstLine, int lineCount)
        {
            if (request.Direction == LiveMemoryIODirection.Write)
            {
                string error = null;
                for (int i = 0; i < lineCount; i++)
                    if (output[firstLine + 1].StartsWith("Error:"))
                    {
                        error = output[firstLine + 1];
                        break;
                    }

                if (error == null)
                    request.Complete(request.WordCount, null);
                else
                    request.Complete(0, error);
            }
            else
            {
                int bytesDone = 0;

                for (int i = 0; i < lineCount; i++)
                {
                    string line = output[firstLine + i];
                    int idx = line.IndexOf(':');
                    if (idx == -1)
                    {
                        request.Complete(0, line);
                        return;
                    }

                    if (!ulong.TryParse(line.Substring(0, idx).Trim(), System.Globalization.NumberStyles.HexNumber, null, out var address))
                    {
                        request.Complete(0, "Invalid address output: " + line);
                        return;
                    }

                    if (address != (request.Address + (uint)bytesDone))
                        throw new Exception($"Mismatching address (expected 0x{request.Address + (uint)bytesDone:x8}: {line})");

                    while (bytesDone < request.ByteCount)
                    {
                        idx++;
                        while (idx < line.Length && line[idx] == ' ')
                            idx++;

                        if (idx >= (line.Length - 2))
                            break;

                        if (!byte.TryParse(line.Substring(idx, 2), System.Globalization.NumberStyles.HexNumber, null, out var byteValue))
                        {
                            request.Complete(0, "Invalid output: " + line);
                            return;
                        }

                        idx += 2;
                        request.Buffer[request.BufferOffset + bytesDone++] = byteValue;
                    }

                }

                if (bytesDone == request.ByteCount || ((request.Direction == LiveMemoryIODirection.PartialRead) && bytesDone > 0))
                    request.Complete((uint)bytesDone / request.WordSize, null);
                else
                {
                    request.Complete(0, $"Failed to read memory at 0x{request.Address:x8}");
                }
            }
        }

        public void Start()
        {
            if (_Tool != null)
                return;

            _Tool = new RedLinkToolClient();
            var probes = _Tool.GetAllProbes();

            if (probes.Length < 2 || _SerialNumber == null)
                _ProbeIndex = 1;
            else
            {
                _ProbeIndex = 0;
                for (int i = 0; i < probes.Length; i++)
                    if (probes[i].SerialNumber == _SerialNumber)
                    {
                        _ProbeIndex = i + 1;
                        break;
                    }

                if (_ProbeIndex == 0)
                    throw new Exception("Could not find a debug probe with the following serial number: " + _SerialNumber);
            }
        }

        public bool TryCauseBreak() => false;

        public void WriteMemory(ulong address, byte[] value, int offset, int length)
        {
            AlignedIORequest[] requests = new[] { new AlignedIORequest { Address = address, WordSize = 1, WordCount = (uint)length, Buffer = value, Direction = LiveMemoryIODirection.Write, BufferOffset = offset } };
            if (!RunMultipleIORequests(requests))
                throw new Exception($"Failed to read memory at {address:x8}");

            if (requests[0].BytesDone != length)
                throw new Exception($"Failed to write memory at {address:x8}");
        }
    }
}
