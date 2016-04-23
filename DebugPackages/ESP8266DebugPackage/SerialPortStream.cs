using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ESP8266DebugPackage
{
    public class SerialPortStream : Stream, IDisposable
    {
        #region PInvoke definitions
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
             [MarshalAs(UnmanagedType.LPTStr)] string filename,
             [MarshalAs(UnmanagedType.U4)] FileAccess access,
             [MarshalAs(UnmanagedType.U4)] FileShare share,
             IntPtr securityAttributes, // optional SECURITY_ATTRIBUTES struct or IntPtr.Zero
             [MarshalAs(UnmanagedType.U4)] FileMode creationDisposition,
             [MarshalAs(UnmanagedType.U4)] FileAttributes flagsAndAttributes,
             IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        static extern bool EscapeCommFunction(SafeFileHandle hFile, CommFunction dwFunc);

        public enum CommFunction : uint
        {
            CLRBREAK = 9,
            CLRDTR = 6,
            CLRRTS = 4,
            SETBREAK = 8,
            SETDTR = 5,
            SETRTS = 3,
            SETXOFF = 1,
            SETXON = 2
        }


        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(SafeHandle hFile,
           [In] IntPtr lpOverlapped,
           out int lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeHandle hFile, IntPtr buf,
           int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(SafeHandle hFile, IntPtr buf,
           int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

        [StructLayout(LayoutKind.Sequential)]
        struct COMMTIMEOUTS
        {
            public UInt32 ReadIntervalTimeout;
            public UInt32 ReadTotalTimeoutMultiplier;
            public UInt32 ReadTotalTimeoutConstant;
            public UInt32 WriteTotalTimeoutMultiplier;
            public UInt32 WriteTotalTimeoutConstant;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetCommTimeouts(SafeHandle hFile, ref COMMTIMEOUTS lpCommTimeouts);

        SafeFileHandle _Handle;

        public enum Parity : byte
        {
            None = 0,
            Odd = 1,
            Even = 2,
            Mark = 3,
            Space = 4,
        }

        public enum StopBits : byte
        {
            One = 0,
            OnePointFive = 1,
            Two = 2
        }

        public enum RtsControl : int
        {
            /// <summary>
            /// Disables the RTS line when the device is opened and leaves it disabled.
            /// </summary>
            Disable = 0,

            /// <summary>
            /// Enables the RTS line when the device is opened and leaves it on.
            /// </summary>
            Enable = 1,

            /// <summary>
            /// Enables RTS handshaking. The driver raises the RTS line when the "type-ahead" (input) buffer
            /// is less than one-half full and lowers the RTS line when the buffer is more than
            /// three-quarters full. If handshaking is enabled, it is an error for the application to
            /// adjust the line by using the EscapeCommFunction function.
            /// </summary>
            Handshake = 2,

            /// <summary>
            /// Specifies that the RTS line will be high if bytes are available for transmission. After
            /// all buffered bytes have been sent, the RTS line will be low.
            /// </summary>
            Toggle = 3
        }

        public enum DtrControl : int
        {
            /// <summary>
            /// Disables the DTR line when the device is opened and leaves it disabled.
            /// </summary>
            Disable = 0,

            /// <summary>
            /// Enables the DTR line when the device is opened and leaves it on.
            /// </summary>
            Enable = 1,

            /// <summary>
            /// Enables DTR handshaking. If handshaking is enabled, it is an error for the application to adjust the line by
            /// using the EscapeCommFunction function.
            /// </summary>
            Handshake = 2
        }


        [StructLayout(LayoutKind.Sequential)]
        internal struct DCB
        {
            internal uint DCBLength;
            internal uint BaudRate;
            private BitVector32 Flags;

            private ushort wReserved;        // not currently used
            internal ushort XonLim;           // transmit XON threshold
            internal ushort XoffLim;          // transmit XOFF threshold             

            internal byte ByteSize;
            internal Parity Parity;
            internal StopBits StopBits;

            internal sbyte XonChar;          // Tx and Rx XON character
            internal sbyte XoffChar;         // Tx and Rx XOFF character
            internal sbyte ErrorChar;        // error replacement character
            internal sbyte EofChar;          // end of input character
            internal sbyte EvtChar;          // received event character
            private ushort wReserved1;       // reserved; do not use     

            private static readonly int fBinary;
            private static readonly int fParity;
            private static readonly int fOutxCtsFlow;
            private static readonly int fOutxDsrFlow;
            private static readonly BitVector32.Section fDtrControl;
            private static readonly int fDsrSensitivity;
            private static readonly int fTXContinueOnXoff;
            private static readonly int fOutX;
            private static readonly int fInX;
            private static readonly int fErrorChar;
            private static readonly int fNull;
            private static readonly BitVector32.Section fRtsControl;
            private static readonly int fAbortOnError;

            static DCB()
            {
                // Create Boolean Mask
                int previousMask;
                fBinary = BitVector32.CreateMask();
                fParity = BitVector32.CreateMask(fBinary);
                fOutxCtsFlow = BitVector32.CreateMask(fParity);
                fOutxDsrFlow = BitVector32.CreateMask(fOutxCtsFlow);
                previousMask = BitVector32.CreateMask(fOutxDsrFlow);
                previousMask = BitVector32.CreateMask(previousMask);
                fDsrSensitivity = BitVector32.CreateMask(previousMask);
                fTXContinueOnXoff = BitVector32.CreateMask(fDsrSensitivity);
                fOutX = BitVector32.CreateMask(fTXContinueOnXoff);
                fInX = BitVector32.CreateMask(fOutX);
                fErrorChar = BitVector32.CreateMask(fInX);
                fNull = BitVector32.CreateMask(fErrorChar);
                previousMask = BitVector32.CreateMask(fNull);
                previousMask = BitVector32.CreateMask(previousMask);
                fAbortOnError = BitVector32.CreateMask(previousMask);

                // Create section Mask
                BitVector32.Section previousSection;
                previousSection = BitVector32.CreateSection(1);
                previousSection = BitVector32.CreateSection(1, previousSection);
                previousSection = BitVector32.CreateSection(1, previousSection);
                previousSection = BitVector32.CreateSection(1, previousSection);
                fDtrControl = BitVector32.CreateSection(2, previousSection);
                previousSection = BitVector32.CreateSection(1, fDtrControl);
                previousSection = BitVector32.CreateSection(1, previousSection);
                previousSection = BitVector32.CreateSection(1, previousSection);
                previousSection = BitVector32.CreateSection(1, previousSection);
                previousSection = BitVector32.CreateSection(1, previousSection);
                previousSection = BitVector32.CreateSection(1, previousSection);
                fRtsControl = BitVector32.CreateSection(3, previousSection);
                previousSection = BitVector32.CreateSection(1, fRtsControl);
            }

            public bool Binary
            {
                get { return Flags[fBinary]; }
                set { Flags[fBinary] = value; }
            }

            public bool CheckParity
            {
                get { return Flags[fParity]; }
                set { Flags[fParity] = value; }
            }

            public bool OutxCtsFlow
            {
                get { return Flags[fOutxCtsFlow]; }
                set { Flags[fOutxCtsFlow] = value; }
            }

            public bool OutxDsrFlow
            {
                get { return Flags[fOutxDsrFlow]; }
                set { Flags[fOutxDsrFlow] = value; }
            }

            public DtrControl DtrControl
            {
                get { return (DtrControl)Flags[fDtrControl]; }
                set { Flags[fDtrControl] = (int)value; }
            }

            public bool DsrSensitivity
            {
                get { return Flags[fDsrSensitivity]; }
                set { Flags[fDsrSensitivity] = value; }
            }

            public bool TxContinueOnXoff
            {
                get { return Flags[fTXContinueOnXoff]; }
                set { Flags[fTXContinueOnXoff] = value; }
            }

            public bool OutX
            {
                get { return Flags[fOutX]; }
                set { Flags[fOutX] = value; }
            }

            public bool InX
            {
                get { return Flags[fInX]; }
                set { Flags[fInX] = value; }
            }

            public bool ReplaceErrorChar
            {
                get { return Flags[fErrorChar]; }
                set { Flags[fErrorChar] = value; }
            }

            public bool Null
            {
                get { return Flags[fNull]; }
                set { Flags[fNull] = value; }
            }

            public RtsControl RtsControl
            {
                get { return (RtsControl)Flags[fRtsControl]; }
                set { Flags[fRtsControl] = (int)value; }
            }

            public bool AbortOnError
            {
                get { return Flags[fAbortOnError]; }
                set { Flags[fAbortOnError] = value; }
            }
        }

        [DllImport("kernel32.dll")]
        static extern bool PurgeComm(SafeFileHandle hFile, uint dwFlags);

        public void Purge()
        {
            if (!PurgeComm(_Handle, 0x0F))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot purge COM port");
        }

        [DllImport("kernel32.dll")]
        static extern bool GetCommState(SafeFileHandle hFile,  ref DCB lpDCB);

        [DllImport("kernel32.dll")]
        static extern bool SetCommState(SafeFileHandle hFile, ref DCB lpDCB);

        #endregion


        public SerialPortStream(string portName, int baudRate, System.IO.Ports.Handshake flowControl)
        {
            _Handle = CreateFile(@"\\.\" + portName, FileAccess.ReadWrite, FileShare.None, IntPtr.Zero, FileMode.Open, FileAttributes.Normal | (FileAttributes)0x40000000, IntPtr.Zero);
            if (_Handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open " + portName);

            ApplyAdvancedSettings(baudRate, flowControl);

            COMMTIMEOUTS timeouts = new COMMTIMEOUTS { ReadIntervalTimeout = 1 };
            if (!SetCommTimeouts(_Handle, ref timeouts))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set timeouts on " + portName);
        }

        public void SetTimeouts(UInt32 readIntervalTimeout, UInt32 readTotalTimeoutMultiplier, UInt32 readTotalTimeoutConstant, UInt32 writeTotalTimeoutMultiplier, UInt32 writeTotalTimeoutConstant)
        {
            COMMTIMEOUTS timeouts = new COMMTIMEOUTS { ReadIntervalTimeout = readIntervalTimeout, ReadTotalTimeoutMultiplier = readTotalTimeoutMultiplier, ReadTotalTimeoutConstant = readTotalTimeoutConstant, WriteTotalTimeoutMultiplier = writeTotalTimeoutMultiplier, WriteTotalTimeoutConstant = writeTotalTimeoutConstant };
            if (!SetCommTimeouts(_Handle, ref timeouts))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set timeouts on a serial port");
        }

        private void ApplyAdvancedSettings(int baudRate, System.IO.Ports.Handshake flowControl)
        {
            DCB dcb = new DCB { DCBLength = (uint)Marshal.SizeOf(typeof(DCB)) };
            if (!GetCommState(_Handle, ref dcb))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query comm state for serial port");

            dcb.BaudRate = (uint)baudRate;
            dcb.ByteSize = 8;
            dcb.Parity = Parity.None;
            dcb.StopBits = StopBits.One;

            switch (flowControl)
            {
                case System.IO.Ports.Handshake.None:
                    dcb.TxContinueOnXoff = false;
                    dcb.OutX = false;
                    dcb.RtsControl = RtsControl.Enable;
                    dcb.DtrControl = DtrControl.Enable;
                    dcb.OutxCtsFlow = false;
                    dcb.OutxDsrFlow = false;
                    break;
                case System.IO.Ports.Handshake.XOnXOff:
                    dcb.TxContinueOnXoff = true;
                    dcb.OutX = true;
                    dcb.RtsControl = RtsControl.Enable;
                    dcb.DtrControl = DtrControl.Enable;
                    dcb.OutxCtsFlow = false;
                    dcb.OutxDsrFlow = false;
                    break;
                case System.IO.Ports.Handshake.RequestToSend:
                    dcb.TxContinueOnXoff = false;
                    dcb.OutX = false;
                    dcb.RtsControl = RtsControl.Handshake;
                    dcb.DtrControl = DtrControl.Enable;
                    dcb.OutxCtsFlow = true;
                    dcb.OutxDsrFlow = false;
                    break;
                case System.IO.Ports.Handshake.RequestToSendXOnXOff:
                    dcb.TxContinueOnXoff = true;
                    dcb.OutX = true;
                    dcb.RtsControl = RtsControl.Handshake;
                    dcb.DtrControl = DtrControl.Enable;
                    dcb.OutxCtsFlow = true;
                    dcb.OutxDsrFlow = false;
                    break;
            }

            if (!SetCommState(_Handle, ref dcb))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set comm state for serial port");
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override void Flush()
        {
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get
            {
                throw new NotSupportedException();
            }
            set
            {
                throw new NotSupportedException();
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        object _ReadLock = new object(), _WriteLock = new object();
        ManualResetEvent _ReadDone = new ManualResetEvent(false);
        ManualResetEvent _WriteDone = new ManualResetEvent(false);

        public bool AllowTimingOutWithZeroBytes { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_ReadLock)
            {
                NativeOverlapped ovl = new NativeOverlapped { EventHandle = _ReadDone.SafeWaitHandle.DangerousGetHandle() };
                IntPtr povl = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
                IntPtr pbuf = Marshal.AllocHGlobal(count);
                int done;
                try
                {
                    for (; ; )
                    {
                        Marshal.StructureToPtr(ovl, povl, false);

                        if (!ReadFile(_Handle, pbuf, count, out done, povl) && Marshal.GetLastWin32Error() != 997)
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read from serial port");

                        _ReadDone.WaitOne();
                        if (!GetOverlappedResult(_Handle, povl, out done, false))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read from serial port");

                        if (done == 0 && !AllowTimingOutWithZeroBytes)
                            continue;

                        Marshal.Copy(pbuf, buffer, offset, done);
                        return done;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(povl);
                    Marshal.FreeHGlobal(pbuf);
                }
            }
        }

        int DoWrite(byte[] buffer, int offset, int count)
        {
            lock (_WriteLock)
            {
                NativeOverlapped ovl = new NativeOverlapped { EventHandle = _WriteDone.SafeWaitHandle.DangerousGetHandle() };
                IntPtr povl = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
                IntPtr pbuf = Marshal.AllocHGlobal(count);
                int done;
                try
                {
                    Marshal.StructureToPtr(ovl, povl, false);
                    Marshal.Copy(buffer, offset, pbuf, count);

                    if (!WriteFile(_Handle, pbuf, count, out done, povl) && Marshal.GetLastWin32Error() != 997)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read from serial port");

                    _WriteDone.WaitOne();
                    if (!GetOverlappedResult(_Handle, povl, out done, false))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read from serial port");

                }
                finally
                {
                    Marshal.FreeHGlobal(povl);
                    Marshal.FreeHGlobal(pbuf);
                }
                return done;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int total = 0; total < count; )
            {
                int done = DoWrite(buffer, offset + total, count - total);
                if (done <= 0)
                    throw new Exception("Unexpected byte count written to serial port: " + done);
                total += done;
            }
        }


        public override void Close()
        {
            _Handle.Close();
            _ReadDone.Set();
            _WriteDone.Set();
            base.Close();
        }

        public void EscapeFunction(CommFunction func)
        {
            if (!EscapeCommFunction(_Handle, func))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot " + func + " on serial port");
        }
    }
}
