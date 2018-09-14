using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VendorSampleParserEngine
{
    public class VersionComparer : IComparer<string>
    {
        int IComparer<string>.Compare(string left, string right) => Compare(left, right);

        public static int Compare(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                if (left == right)
                    return 0;
                else if (string.IsNullOrEmpty(left))
                    return -1;
                else
                    return 1;
            }

            string[] leftComponents = left.Split('.');
            string[] rightComponents = right.Split('.');
            for (int i = 0; i < Math.Max(leftComponents.Length, rightComponents.Length); i++)
            {
                if (i >= leftComponents.Length)
                    return -1;
                if (i >= rightComponents.Length)
                    return 1;

                int i1, i2;
                if (int.TryParse(leftComponents[i], out i1) && int.TryParse(rightComponents[i], out i2))
                {
                    if (i1 != i2)
                        return i1 - i2;
                }
                else
                {
                    int cmp = StringComparer.InvariantCultureIgnoreCase.Compare(leftComponents[i], rightComponents[i]);
                    if (cmp != 0)
                        return cmp;
                }
            }

            return 0;
        }
    }
}
