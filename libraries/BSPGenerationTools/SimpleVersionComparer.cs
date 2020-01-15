using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BSPGenerationTools
{
    public class SimpleVersionComparer : IComparer<string>
    {
        public SimpleVersionComparer()
        {
        }

        static string[] SplitVersionIntoComponents(string version)
        {
            List<string> result = new List<string>();
            int start = -1;
            bool isNumber = false;

            for (int i = 0; i < version.Length; i++)
            {
                bool thisIsNumber = char.IsNumber(version[i]);
                if (start != -1 && thisIsNumber != isNumber)
                    result.Add(version.Substring(start, i - start));
                if (start == -1 || thisIsNumber != isNumber)
                    start = i;
                isNumber = thisIsNumber;
            }

            if (start < version.Length)
                result.Add(version.Substring(start));

            return result.ToArray();
        }

        public int Compare(string left, string right)
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

            string[] leftComponents = SplitVersionIntoComponents(left);
            string[] rightComponents = SplitVersionIntoComponents(right);
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
