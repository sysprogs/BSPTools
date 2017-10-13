using BSPEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPComparer
{
    class Program
    {
        static void CompareField(MCU m1, MCU m2, string fieldName)
        {
            object o1 = typeof(MCU).GetField(fieldName).GetValue(m1);
            object o2 = typeof(MCU).GetField(fieldName).GetValue(m2);
            if (!o1.Equals(o2))
                Console.WriteLine($"Mismatching {fieldName} for {m1.ID}: {o1:x} => {o2:x}");
        }

        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine($"Usage: BSPComparer <old BSP.xml> <new BSP.xml>");
                return;
            }

            var oldBSP = XmlTools.LoadObject<BoardSupportPackage>(args[0]);
            var newBSP = XmlTools.LoadObject<BoardSupportPackage>(args[1]);

            var oldDict = oldBSP.SupportedMCUs.ToDictionary(m => m.ID);
            var newDict = newBSP.SupportedMCUs.ToDictionary(m => m.ID);

            Console.WriteLine("BSP changes:");

            var removedMCUs = oldDict.Keys.Except(newDict.Keys).ToArray();
            var addedMCUs  = newDict.Keys.Except(oldDict.Keys).ToArray();
            Console.WriteLine($"Removed {removedMCUs.Length} MCUs");
            foreach (var mcu in removedMCUs)
                Console.WriteLine($"\t{mcu}");

            Console.WriteLine($"Added {addedMCUs.Length} new MCUs");
            foreach (var mcu in addedMCUs)
                Console.WriteLine($"\t{mcu}");

            foreach (var key in oldDict.Keys.Intersect(newDict.Keys))
            {
                var oldMCU = oldDict[key];
                var newMCU = newDict[key];
                CompareField(oldMCU, newMCU, nameof(MCU.FLASHSize));
                CompareField(oldMCU, newMCU, nameof(MCU.RAMSize));
                CompareField(oldMCU, newMCU, nameof(MCU.FLASHBase));
                CompareField(oldMCU, newMCU, nameof(MCU.RAMBase));
            }
        }
    }
}
