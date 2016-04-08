using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace ESP8266DebugPackage
{
    class ESP8266BootloaderClient
    {
        private SerialPortStream _Port;
        private readonly int _ResetDelay;

        public string _ResetSequence;

        public const string DefaultResetSequence = "!DTR;RTS;SLEEP;DTR;!RTS;SLEEP;!DTR;SLEEP";

        public ESP8266BootloaderClient(SerialPortStream port, int resetDelay, string resetSequence)
        {
            _Port = port;
            _ResetDelay = resetDelay;
            _ResetSequence = resetSequence ?? DefaultResetSequence;
        }

        enum Command : byte
        {
            ESP_NO_COMMAND = 0,
            ESP_FLASH_BEGIN = 0x02,
            ESP_FLASH_DATA = 0x03,
            ESP_FLASH_END = 0x04,
            ESP_MEM_BEGIN = 0x05,
            ESP_MEM_END = 0x06,
            ESP_MEM_DATA = 0x07,
            ESP_SYNC = 0x08,
            ESP_WRITE_REG = 0x09,
            ESP_READ_REG = 0x0a,
        }

        const int ESP_FLASH_BLOCK = 0x400;

        KeyValuePair<int, byte[]> RunCommand(Command op, byte[] data = null, int chk = 0)
        {
            if (op != Command.ESP_NO_COMMAND)
            {
                byte[] cmdBlock = new byte[8 + ((data == null) ? 0 : data.Length)];
                cmdBlock[0] = 0;
                cmdBlock[1] = (byte)op;
                BitConverter.GetBytes((ushort)((data == null) ? 0 : data.Length)).CopyTo(cmdBlock, 2);
                BitConverter.GetBytes(chk).CopyTo(cmdBlock, 4);
                if (data != null)
                    data.CopyTo(cmdBlock, 8);

#if DEBUG
                StringBuilder sb = new StringBuilder();
                foreach (var b in cmdBlock)
                    sb.AppendFormat("{0:X2}", b);
                System.Diagnostics.Debug.WriteLine(sb.ToString());
#endif

                EscapeAndSend(cmdBlock);
            }

            byte[] result = new byte[8];
            if (_Port.Read(result, 0, 1) != 1)
                throw new Exception("Timeout reading reply");
            if (result[0] != 0xc0)
                throw new Exception("Unexpected reply from ESP8266");

            if (ReceiveAndUnescape(result, 0, 8) != 8)
                throw new Exception("Failed to read reply header from ESP8266");

            if (result[0] != 1 || (op != 0 && result[1] != (byte)op))
                throw new Exception("Mismatching reply from ESP8266");

            ushort length = BitConverter.ToUInt16(result, 2);
            int scalarOutput = BitConverter.ToInt32(result, 4);
            result = new byte[length];
            if (ReceiveAndUnescape(result, 0, result.Length) != result.Length)
                throw new Exception("Cannot read reply body");

            byte[] tmp = new byte[1];
            if (_Port.Read(tmp, 0, 1) != 1 || tmp[0] != 0xc0)
                throw new Exception("Missing end of packet");

            return new KeyValuePair<int, byte[]>(scalarOutput, result);
        }

        private int ReceiveAndUnescape(byte[] data, int offset, int length)
        {
            byte[] tmp = new byte[1];
            for (int i = 0; i < length; i++)
            {
                if (_Port.Read(tmp, 0, 1) != 1)
                    return i;
                if (tmp[0] != 0xdb)
                    data[offset + i] = tmp[0];
                else
                {
                    if (_Port.Read(tmp, 0, 1) != 1)
                        return i;
                    if (tmp[0] == 0xdc)
                        data[offset + i] = 0xc0;
                    else if (tmp[0] == 0xdd)
                        data[offset + i] = 0xdb;
                    else
                        throw new Exception("Invalid SLIP escape");
                }
            }
            return length;
        }

        private void EscapeAndSend(byte[] cmdBlock)
        {
            List<byte> packet = new List<byte> { Capacity = cmdBlock.Length * 2 };
            packet.Add(0xc0);
            foreach (var b in cmdBlock)
            {
                if (b == 0xdb)
                {
                    packet.Add(0xdb);
                    packet.Add(0xdd);
                }
                else if (b == 0xc0)
                {
                    packet.Add(0xdb);
                    packet.Add(0xdc);
                }
                else
                    packet.Add(b);
            }
            packet.Add(0xc0);
            _Port.Write(packet.ToArray(), 0, packet.Count);
        }

        public void Sync()
        {
            _Port.SetTimeouts(100, 1, 100, 0, 0);

            List<byte> syncMagic = new List<byte> { 0x07, 0x07, 0x12, 0x20 };
            for (int i = 0; i < 32; i++)
                syncMagic.Add(0x55);

            for (int outerIter = 0; ; outerIter++)
            {
                for (int innerIter = 0; innerIter < 5; innerIter++)
                {
                    try
                    {
                        if (innerIter == 0)
                        {
                            foreach(string cmd in _ResetSequence.Split(';'))
                            {
                                switch(cmd)
                                {
                                    case "DTR":
                                        _Port.EscapeFunction(SerialPortStream.CommFunction.SETDTR);
                                        break;
                                    case "!DTR":
                                        _Port.EscapeFunction(SerialPortStream.CommFunction.CLRDTR);
                                        break;
                                    case "RTS":
                                        _Port.EscapeFunction(SerialPortStream.CommFunction.SETRTS);
                                        break;
                                    case "!RTS":
                                        _Port.EscapeFunction(SerialPortStream.CommFunction.CLRRTS);
                                        break;
                                    case "SLEEP":
                                        Thread.Sleep(_ResetDelay);
                                        break;
                                    case "":
                                        break;
                                    default:
                                        if (cmd.StartsWith("SLEEP:"))
                                            Thread.Sleep(int.Parse(cmd.Substring(6)));
                                        else
                                            throw new Exception("Invalid reset command: " + cmd);
                                        break;
                                }
                            }
                        }

                        _Port.Purge();
                        RunCommand(Command.ESP_SYNC, syncMagic.ToArray());
                        for (int j = 0; j < 7; j++)
                            RunCommand(Command.ESP_NO_COMMAND);
                        _Port.SetTimeouts(5000, 0, 5000, 0, 0);
                        return;
                    }
                    catch
                    {
                        if (outerIter >= 4)
                            throw;
                    }
                }
            }
        }

        static byte[] PackIntegers(params int[] integers)
        {
            byte[] data = new byte[4 * integers.Length];
            for (int i = 0; i < integers.Length; i++)
                BitConverter.GetBytes(integers[i]).CopyTo(data, i * 4);
            return data;
        }


        void StartFLASH(int offset, int sizeInBytes)
        {
            int numBlocks = (sizeInBytes + ESP_FLASH_BLOCK - 1) / ESP_FLASH_BLOCK;
            byte[] data = PackIntegers(sizeInBytes, numBlocks, ESP_FLASH_BLOCK, offset);
            var result = RunCommand(Command.ESP_FLASH_BEGIN, data);
            if (result.Value.Length != 2 || result.Value[0] != 0 || result.Value[1] != 0)
                throw new Exception("Failed to start FLASH operation");
        }

        void StartRAM(int size, int blocks, int blocksize, int offset)
        {
            byte[] data = PackIntegers(size, blocks, blocksize, offset);
            var result = RunCommand(Command.ESP_MEM_BEGIN, data);
            if (result.Value.Length != 2 || result.Value[0] != 0 || result.Value[1] != 0)
                throw new Exception("Failed to start RAM operation");
        }

        void EndRAM(int entry)
        {
            byte[] data = PackIntegers((entry == 0) ? 1 : 0, entry);
            var result = RunCommand(Command.ESP_MEM_END, data);
            if (result.Value.Length != 2 || result.Value[0] != 0 || result.Value[1] != 0)
                throw new Exception("Failed to end RAM operation");
        }

        public void RunProgram(bool usesDIO, bool reboot = false)
        {
            /*if (usesDIO)  //This does not seem to work
            {
                StartFLASH(0, 0);
                StartRAM(0, 0, 0, 0x40100000);
                EndRAM(0x40000080);
            }
            else*/
            {
                byte[] data = PackIntegers(reboot ? 0 : 1);
                var result = RunCommand(Command.ESP_FLASH_END, data);
                if (result.Value.Length != 2 || result.Value[0] != 0 || result.Value[1] != 0)
                    throw new Exception("Failed to finish FLASH operation");
            }
        }

        static byte ComputeChecksum(byte[] data, int offset, int length)
        {
            byte result = 0xef;
            for (int i = 0; i < length; i++)
                result ^= data[offset + i];
            return result;
        }

        public delegate void BlockWrittenHandler(ESP8266BootloaderClient sender, uint address, int blockSize);
        public event BlockWrittenHandler BlockWritten;

        void WriteFLASHBlock(uint baseAddr, byte[] data, int offset, int length, int seq)
        {
            byte[] packet = PackIntegers(length, seq, 0, 0);
            int headerLen = packet.Length;
            Array.Resize(ref packet, headerLen + length);
            Array.Copy(data, offset, packet, headerLen, Math.Min(length, data.Length - offset));
            for (int i = headerLen + (data.Length - offset); i < packet.Length; i++)
                packet[i] = 0xff;

            var result = RunCommand(Command.ESP_FLASH_DATA, packet, ComputeChecksum(packet, headerLen, length));
            if (result.Value.Length != 2 || result.Value[0] != 0 || result.Value[1] != 0)
                throw new Exception("Failed to send FLASH contents");
            if (BlockWritten != null)
                BlockWritten(this, (uint)(baseAddr + offset), Math.Min(length, data.Length - offset));
        }

        public void ProgramFLASH(uint address, byte[] data)
        {
            int blockCount = (data.Length + ESP_FLASH_BLOCK - 1) / ESP_FLASH_BLOCK;
            StartFLASH((int)address, blockCount * ESP_FLASH_BLOCK);
            for (int seq = 0; seq < blockCount; seq++)
                WriteFLASHBlock(address, data, seq * ESP_FLASH_BLOCK, ESP_FLASH_BLOCK, seq);
        }
    }
}
