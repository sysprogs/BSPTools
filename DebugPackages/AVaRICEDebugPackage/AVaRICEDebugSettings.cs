using BSPEngine;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace AVaRICEDebugPackage
{
    [XmlType("com.visualgdb.edp.avarice.settings")]
    public class AVaRICEDebugSettings : INotifyPropertyChanged
    {
        string _DebugAdapterType = "-g";
        public string DebugAdapterType
        {
            get => _DebugAdapterType;
            set
            {
                _DebugAdapterType = value;
                OnPropertyChanged(nameof(DebugAdapterType));
            }
        }

        string _DebugInterface = "";
        public string DebugInterface
        {
            get => _DebugInterface;
            set
            {
                _DebugInterface = value;
                OnPropertyChanged(nameof(DebugInterface));
            }
        }

        string _DebugBitrate = "125K";
        public string DebugBitrate
        {
            get => _DebugBitrate;
            set
            {
                _DebugBitrate = value;
                OnPropertyChanged(nameof(DebugBitrate));
            }
        }

        string _DebugPort = "USB";
        public string DebugPort
        {
            get => _DebugPort;
            set
            {
                _DebugPort = value;
                OnPropertyChanged(nameof(DebugPort));
            }
        }

        bool _EraseFLASH = true;
        public bool EraseFLASH
        {
            get => _EraseFLASH;
            set
            {
                _EraseFLASH = value;
                OnPropertyChanged(nameof(EraseFLASH));
            }
        }

        bool _ProgramFLASH = true;
        public bool ProgramFLASH
        {
            get => _ProgramFLASH;
            set
            {
                _ProgramFLASH = value;
                OnPropertyChanged(nameof(ProgramFLASH));
            }
        }

        bool _VerifyFLASH = false;
        public bool VerifyFLASH
        {
            get => _VerifyFLASH;
            set
            {
                _VerifyFLASH = value;
                OnPropertyChanged(nameof(VerifyFLASH));
            }
        }

        string _ExtraArguments;

        public string ExtraArguments
        {
            get => _ExtraArguments;
            set
            {
                _ExtraArguments = value;
                OnPropertyChanged(nameof(ExtraArguments));
            }
        }

        public int GDBTimeout = 60;

        public AVaRICEDebugSettings Clone() => (AVaRICEDebugSettings)MemberwiseClone();

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
