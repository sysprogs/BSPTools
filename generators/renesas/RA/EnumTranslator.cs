using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace renesas_ra_bsp_generator
{
    class EnumTranslator
    {
        class EnumContext
        {
            public Dictionary<string, PropertyEntry.Enumerated.Suggestion> ValuesByID = new Dictionary<string, PropertyEntry.Enumerated.Suggestion>();
            public bool HasEmptyValue, DefaultValueInconsistent;
            public string DefaultValue;

            public PropertyEntry.Enumerated.Suggestion[] BuildSuggestionList()
            {
                IEnumerable<PropertyEntry.Enumerated.Suggestion> result = ValuesByID.Values;
                if (HasEmptyValue && result.FirstOrDefault(e => e.InternalValue == "") == null)
                    result = result.Append(new PropertyEntry.Enumerated.Suggestion { InternalValue = "", UserFriendlyName = "(empty)" });

                return result.ToArray();
            }
        }

        Dictionary<string, EnumContext> _Enums = new Dictionary<string, EnumContext>();

        static string MakeDefaultValueID(string enumID) => "_default." + enumID;

        public void ProcessEnumDefinitions(XmlDocument xml, List<SysVarEntry> fixedValues)
        {
            foreach (var ed in xml.DocumentElement.SelectElements("bsp/enum"))
            {
                var eid = ed.GetStringAttribute("id");

                var options = ed.SelectElements("option").ToArray();
                if (options.Length < 1)
                    continue;

                var defaultValue = ed.TryGetStringAttribute("default");

                if (!_Enums.TryGetValue(eid, out var ectx))
                    _Enums[eid] = ectx = new EnumContext();

                if (string.IsNullOrEmpty(defaultValue))
                    ectx.HasEmptyValue = true;

                string translatedDefaultValue = null;
                foreach (var o in ed.SelectElements("option"))
                {
                    var oi = o.GetStringAttribute("id");
                    var ov = o.GetStringAttribute("value");
                    var od = o.GetStringAttribute("display");
                    if (oi == defaultValue)
                        translatedDefaultValue = ov;

                    if (ectx.ValuesByID.TryGetValue(oi, out var v) && v.InternalValue != ov)
                    {
                        //Debug.WriteLine($"Inconsistent enum value for {eid}/{oi}: {ov} != {v.InternalValue}");
                    }

                    ectx.ValuesByID[oi] = new PropertyEntry.Enumerated.Suggestion { InternalValue = ov, UserFriendlyName = od };
                }

                if (!string.IsNullOrEmpty(translatedDefaultValue))
                {
                    fixedValues.Add(new SysVarEntry { Key = MakeDefaultValueID(eid), Value = translatedDefaultValue });
                    if (ectx.DefaultValue == null)
                        ectx.DefaultValue = translatedDefaultValue;
                    else if (ectx.DefaultValue != translatedDefaultValue)
                        ectx.DefaultValueInconsistent = true;
                }
            }
        }

        struct PendingEnumExpansion
        {
            public PropertyEntry.Enumerated Entry;
            public string[] EnumIDs;
            public string DefaultValue;
        }

        List<PendingEnumExpansion> _PendingEnumExpansions = new List<PendingEnumExpansion>();


        public PropertyEntry.Enumerated CreatePendingEntryForEnum(string[] eids, string defaultValue)
        {
            var ee = new PropertyEntry.Enumerated
            {
                SuggestionList = new PropertyEntry.Enumerated.Suggestion[0],
            };

            _PendingEnumExpansions.Add(new PendingEnumExpansion { Entry = ee, EnumIDs = eids, DefaultValue = defaultValue });
            return ee;
        }

        public void ExpandEnumReferences(BSPReportWriter report, string[] allInterruptNames)
        {
            foreach (var ee in _PendingEnumExpansions)
            {
                string specialEnum = null;
                if (ee.EnumIDs.Length != 1)
                {
                    if (ee.EnumIDs.Length == 2)
                        specialEnum = ee.EnumIDs[1];

                    if (specialEnum != "_signal" && specialEnum != "_interrupt")
                        throw new Exception("Unexpected multiple enums referenced by the property");
                }

                var enumID = ee.EnumIDs[0];
                if (_Enums.TryGetValue(enumID, out var ectx))
                {
                    ee.Entry.SuggestionList = ectx.BuildSuggestionList();
                    if (ectx.DefaultValue != null)
                        ee.Entry.DefaultEntryIndex = Enumerable.Range(0, ee.Entry.SuggestionList.Length).FirstOrDefault(i => ee.Entry.SuggestionList[i].InternalValue == ectx.DefaultValue);
                    if (ectx.DefaultValueInconsistent)
                        ee.Entry.DefaultEntryValue = $"$${MakeDefaultValueID(enumID)}$$";
                }
                else
                    report.ReportMergeableError("Unknown enum:", enumID);

                if (specialEnum == "_interrupt")
                {
                    ee.Entry.SuggestionList = ee.Entry.SuggestionList.Concat(allInterruptNames.OrderBy(n => n).Select(n => new PropertyEntry.Enumerated.Suggestion { InternalValue = n })).ToArray();
                }
            }
        }
    }
}
