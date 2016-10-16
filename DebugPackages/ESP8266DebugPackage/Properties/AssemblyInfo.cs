using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
#if ESP32
[assembly: AssemblyTitle("ESP32DebugPackage")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("ESP32DebugPackage")]
[assembly: AssemblyCopyright("Copyright ©  2015")]
[assembly: Guid("DFB4E45F-470F-4B00-99B3-8E66E4EC628E")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.

// The following GUID is for the ID of the typelib if this project is exposed to COM

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers 
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
#else
[assembly: AssemblyTitle("ESP8266DebugPackage")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("ESP8266DebugPackage")]
[assembly: AssemblyCopyright("Copyright ©  2015")]
[assembly: Guid("6480005c-0955-4d7b-a6d4-789feb6ef3dc")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
#endif
[assembly: ComVisible(false)]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
