"""
    This file generates a BSP file that makes VisualGDB recognize the Mbed library and automatically create Visual Studio
    projects for it
"""
import sys
import os
import re
from os.path import join, abspath, dirname, exists, basename
import workspace_tools.project
ROOT = abspath(join(dirname(workspace_tools.project.__file__), ".."))
sys.path.insert(0, ROOT)

from shutil import move, rmtree
from optparse import OptionParser

from workspace_tools.paths import EXPORT_DIR, EXPORT_WORKSPACE, EXPORT_TMP
from workspace_tools.paths import MBED_BASE, MBED_LIBRARIES
from workspace_tools.export import export, setup_user_prj, EXPORTERS, mcu_ide_matrix, online_build_url_resolver
from workspace_tools.export.exporters import Exporter
from workspace_tools.utils import args_error
from workspace_tools.tests import TESTS, Test, TEST_MAP
from workspace_tools.libraries import LIBRARIES
from workspace_tools.targets import TARGET_NAMES, EXPORT_MAP, TARGET_MAP, TARGETS
from workspace_tools.toolchains import TOOLCHAIN_CLASSES
import xml.etree.ElementTree as ET

try:
    import workspace_tools.private_settings as ps
except:
    ps = object()

class LibraryBuilder:
    def __init__(self, lib):
        self.sourceConditionMap = {}
        self.headerConditionMap = {}
        self.includeDirConditionMap = {}
        self.ID = lib['id']
        self.Macros = lib.get('macros', None)
        self.SupportedTargets = {}
        self.Dependencies = lib['dependencies']

    def append_resources(self, target, resources):
        found = False
        for (items, map, isPath) in [[resources.c_sources + resources.cpp_sources + resources.s_sources, self.sourceConditionMap, True], 
            [resources.headers, self.headerConditionMap, True], 
            [resources.inc_dirs, self.includeDirConditionMap, True]]:
            for fn in items:
                if isPath:
                    fn = "$$SYS:BSP_ROOT$$/" + fn.replace("\\", "/")
                    found = True
                map.setdefault(fn, set([])).add(target)
        if found:
            self.SupportedTargets[target] = True

Exporter = EXPORTERS['gcc_arm']
def scan_dir_contents(project_dir, toolchain):
    resources = toolchain.scan_resources(project_dir)
    resources.relative_to(ROOT, False)
    return resources

def make_node(name, text):
    str = ET.Element(name)
    str.text = text
    str.tail = "\n"
    return str

def append_node(el, name):
    node = ET.Element(name)
    el.append(node)
    return node

def provide_node(el, name):
    node = el.find(name)
    if node == None:
        node = ET.Element(name)
        el.append(node)
    return node

class MemoryDefinition:
    def __init__(self, name, addr, size):
        self.Name = name
        self.Start = addr
        self.Size = size

def add_file_condition(libBuilder, fwNode, condList, fileRegex, conditionID, conditionName):
    propList = provide_node(provide_node(provide_node(provide_node(fwNode, "ConfigurableProperties"), "PropertyGroups"), "PropertyGroup"), "Properties")
    ET.SubElement(propList, "PropertyEntry", {"xsi:type":"Boolean"}).extend([make_node("Name", conditionName), make_node("UniqueID", conditionID), make_node("ValueForTrue", "1")])
    pattern = re.compile(fileRegex)

    for fn in libBuilder.sourceConditionMap.keys() + libBuilder.headerConditionMap.keys():
        if not pattern.match(fn.replace("$$SYS:BSP_ROOT$$/", "")):
            continue
        fileCondNode = ET.SubElement(condList, "FileCondition")
        condNode = ET.SubElement(fileCondNode, "ConditionToInclude", {"xsi:type":"Equals"})
        condNode.append(make_node("Expression", "$$"+conditionID+"$$"))
        condNode.append(make_node("ExpectedValue", "1"))
        fileCondNode.append(make_node("FilePath", fn))

def parse_mem_size(str):
    rgSub = re.compile("[( ]*([0-9a-fA-FxKkMm]+) *([+-]) *([0-9a-fA-FxKkMm]+)[) ]*")
    str = str.strip('()\n')

    m = rgSub.match(str)
    if m != None:
        if m.group(2) == '+':
            return parse_mem_size(m.group(1)) + parse_mem_size(m.group(3))
        else:
            return parse_mem_size(m.group(1)) - parse_mem_size(m.group(3))

    multiplier = 1
    if (str[-1].upper() == 'K'):
        multiplier = 1024
        str = str[:-1]
    elif (str[-1].upper() == 'M'):
        multiplier = 1024 * 1024
        str = str[:-1]

    if str[0:2] == "0x":
        return int(str, 16) * multiplier
    else:
        return int(str, 10) * multiplier

def parse_linker_script(ldsFile):
    insideMemoryBlock = False
    rgMemDef = re.compile(' *([^ ]+) *\([^()]+\) *: *ORIGIN *= *([^ ,]+), *LENGTH *= *([^/]*)($|/\\*)')

    result = []
    with open(ldsFile, 'r') as f:
        for line in f:
            if not insideMemoryBlock and line.strip() == "MEMORY":
                insideMemoryBlock = True
            elif insideMemoryBlock and line.strip() == "}":
                break
            elif insideMemoryBlock:
                m = rgMemDef.match(line)
                if m != None:
                    mem = MemoryDefinition(m.group(1), parse_mem_size(m.group(2)), parse_mem_size(m.group(3)))
                    if mem.Size != 0:
                        result.append(mem)
    return result

sourceConditionMap = {}
headerConditionMap = {}
symbolConditionMap = {}
includeDirConditionMap = {}
srcDirToLibMap = {}
resMap = {}
libBuilderMap = {}

LIBRARY_NAMES = {
    "cpputest":"CppUTest",
    "usb_host":"USB Host support",
    "usb":"USB Device support",
    "ublox":"U-blox drivers",
    "rtos":"RTOS abstraction layer",
    "dsp":"DSP Library",
    "rpc":"RPC Support",
    "fat":"FAT File System support",
    "cmsis_dsp":"CMSIS DSP",
    "eth":"Ethernet support",
    "rtx":"Keil RTX RTOS",
}

print("Parsing targets...")
xml = ET.parse(join(dirname(__file__), 'bsp_template.xml'))
mcus = xml.find("SupportedMCUs")
family = xml.find("MCUFamilies/MCUFamily")

targetCount = 0
for target in Exporter.TARGETS:
    print("\t" + target + "...")
    toolchain = TOOLCHAIN_CLASSES['GCC_ARM'](TARGET_MAP[target.upper()])
    resources = scan_dir_contents(os.path.join(ROOT, 'libraries/mbed'), toolchain)
    resources.toolchain = toolchain
    for (items, map, isPath) in [[resources.c_sources + resources.cpp_sources + resources.s_sources, sourceConditionMap, True], 
               [resources.headers, headerConditionMap, True], 
               [resources.inc_dirs, includeDirConditionMap, True],
               [toolchain.get_symbols(), symbolConditionMap, False]]:
        for fn in items:
            if isPath:
                fn = "$$SYS:BSP_ROOT$$/" + fn.replace("\\", "/")
            map.setdefault(fn, []).append(target)
    targetCount = targetCount + 1
    resMap[target] = resources

    for lib in LIBRARIES:
        srcs = lib['source_dir']
        if isinstance(srcs, basestring):
            srcs = [srcs]
        for src in srcs:
            libBuilderMap.setdefault(lib['id'], LibraryBuilder(lib)).append_resources(target, scan_dir_contents(src, toolchain))
            srcDirToLibMap[src] = lib['id']

for fw in libBuilderMap.values():
    fw.DependencyIDs = set([])
    for dep in fw.Dependencies:
        id = srcDirToLibMap.get(dep)
        if id != None:
            fw.DependencyIDs.add(id)

#Set flags different for each target
for target in Exporter.TARGETS:
    mcu = ET.Element("MCU")
    mcu.append(make_node("ID", target))
    mcu.append(make_node("HierarchicalPath", "Mbed"))
    mcu.append(make_node("FamilyID", family.find("ID").text))
    flags = append_node(mcu,"CompilationFlags")
    for (node, dict) in [[append_node(mcu, "AdditionalSourceFiles"), sourceConditionMap],
                         [append_node(mcu, "AdditionalHeaderFiles"), headerConditionMap],
                         [append_node(flags, "IncludeDirectories"), includeDirConditionMap],
                         [append_node(flags, "PreprocessorMacros"), symbolConditionMap]]:
        for (file,targets) in dict.items():
            if len(targets) < targetCount and target in targets:
                node.append(make_node("string", file))

    resources = resMap.get(target, None)
    if resources == None:
        continue

    flagList = resources.toolchain.cpu[:]
    if "-mfloat-abi=softfp" in flagList:
        flagList.remove("-mfloat-abi=softfp")
        flagList.append("$$com.sysprogs.bspoptions.arm.floatmode$$")
        propList = provide_node(provide_node(provide_node(provide_node(mcu, "ConfigurableProperties"), "PropertyGroups"), "PropertyGroup"), "Properties")
        propEl = ET.SubElement(propList, "PropertyEntry", {"xsi:type":"Enumerated"})
        propEl.extend([make_node("Name", "Floating point support"), make_node("UniqueID", "com.sysprogs.bspoptions.arm.floatmode"), make_node("DefaultEntryIndex", "2")])
        listEl = ET.SubElement(propEl, "SuggestionList")
        ET.SubElement(listEl, "Suggestion").extend([make_node("UserFriendlyName", "Software"), make_node("InternalValue", "-mfloat-abi=soft")])
        ET.SubElement(listEl, "Suggestion").extend([make_node("UserFriendlyName", "Hardware"), make_node("InternalValue", "-mfloat-abi=hard")])
        ET.SubElement(listEl, "Suggestion").extend([make_node("UserFriendlyName", "Hardware with Software interface"), make_node("InternalValue", "-mfloat-abi=softfp")])
        ET.SubElement(listEl, "Suggestion").extend([make_node("UserFriendlyName", "Unspecified"), make_node("InternalValue", "")])

    ET.SubElement(flags, "COMMONFLAGS").text = " ".join(flagList)
    ET.SubElement(flags, "LinkerScript").text = "$$SYS:BSP_ROOT$$/" + resources.linker_script.replace("\\", "/")
    mems = parse_linker_script(os.path.join(ROOT, resources.linker_script))
    mcu.append(make_node("RAMSize", str(sum([m.Size for m in mems if ("RAM" in m.Name.upper())]))))
    mcu.append(make_node("FLASHSize", str(sum([m.Size for m in mems if ("FLASH" in m.Name.upper())]))))

    memList = ET.SubElement(ET.SubElement(mcu, "MemoryMap"), "Memories")
    for mem in mems:
        memEl = ET.SubElement(memList, "MCUMemory")
        memEl.append(make_node("Name", mem.Name))
        memEl.append(make_node("Address", str(mem.Start)))
        memEl.append(make_node("Size", str(mem.Size)))
        if mem.Name.upper() == "FLASH":
            memEl.append(make_node("Flags", "IsDefaultFLASH"))
        if mem.Name.upper() == "RAM":
            memEl.append(make_node("LoadedFromMemory", "FLASH"))

    mcus.append(mcu)

#Set flags shared between targets
flags = append_node(family,"CompilationFlags")
for (node, dict) in [[append_node(family, "AdditionalSourceFiles"), sourceConditionMap],
                        [append_node(family, "AdditionalHeaderFiles"), headerConditionMap],
                        [append_node(flags, "IncludeDirectories"), includeDirConditionMap],
                        [append_node(flags, "PreprocessorMacros"), symbolConditionMap]]:
    for (file,targets) in dict.items():
        if len(targets) == targetCount:
            node.append(make_node("string", file))


family.find("AdditionalSourceFiles").append(make_node("string", "$$SYS:BSP_ROOT$$/stubs.cpp"))
condList = xml.find("FileConditions")
flagCondList = xml.find("ConditionalFlags")

#Add frameworks
for lib in libBuilderMap.values():
    fw = ET.SubElement(xml.find("Frameworks"), "EmbeddedFramework")
    if len(lib.SupportedTargets) != targetCount:
        fw.append(make_node("MCUFilterRegex", "|".join(lib.SupportedTargets.keys())))
        
    fw.append(make_node("ID", "com.sysprogs.arm.mbed." + lib.ID))
    fw.append(make_node("ProjectFolderName", lib.ID))
    fw.append(make_node("UserFriendlyName", LIBRARY_NAMES.get(lib.ID, lib.ID + " library")))
    ET.SubElement(fw, "AdditionalSourceFiles").extend([make_node("string", fn) for fn in lib.sourceConditionMap.keys()]) 
    ET.SubElement(fw, "AdditionalHeaderFiles").extend([make_node("string", fn) for fn in lib.headerConditionMap.keys()]) 
    ET.SubElement(fw, "AdditionalIncludeDirs").extend([make_node("string", fn) for (fn, cond) in lib.includeDirConditionMap.items() if len(cond) == len(lib.SupportedTargets)])
    if (len(lib.DependencyIDs) > 0):
        ET.SubElement(fw, "RequiredFrameworks").extend([make_node("string", "com.sysprogs.arm.mbed." + id) for id in lib.DependencyIDs])
    #ET.SubElement(ET.SubElement(fw, "AdditionalSystemVars"), "SysVarEntry").extend([make_node("Key", "com.sysprogs.arm.mbed." + lib.ID + ".included"), make_node("Value", "1")])
    if lib.Macros != None:
        ET.SubElement(fw, "AdditionalPreprocessorMacros").extend([make_node("string", m) for m in lib.Macros])

    for (fn, cond) in lib.sourceConditionMap.items() + lib.headerConditionMap.items():
        if len(cond) == len(lib.SupportedTargets):
            continue
        if len(cond) > len(lib.SupportedTargets):
            raise AssertionError("Source file condition list longer than the framework condition list. Check how the framework conditions are formed.")
        fileCondNode = ET.SubElement(condList, "FileCondition")
        condNode = ET.SubElement(fileCondNode, "ConditionToInclude", {"xsi:type":"MatchesRegex"})
        condNode.append(make_node("Expression", "$$SYS:MCU_ID$$"))
        condNode.append(make_node("Regex", "|".join(cond)))
        fileCondNode.append(make_node("FilePath", fn))

    for (dir, cond) in lib.includeDirConditionMap.items():
        if len(cond) == len(lib.SupportedTargets):
            continue
        if len(cond) > len(lib.SupportedTargets):
            raise AssertionError("Source file condition list longer than the framework condition list. Check how the framework conditions are formed.")
        flagCondNode = ET.SubElement(flagCondList, "ConditionalToolFlags")
        condListNode = ET.SubElement(ET.SubElement(flagCondNode, "FlagCondition", {"xsi:type":"And"}), "Arguments")
        ET.SubElement(condListNode, "Condition", {"xsi:type":"ReferencesFramework"}).append(make_node("FrameworkID", "com.sysprogs.arm.mbed." + lib.ID))
        ET.SubElement(condListNode, "Condition", {"xsi:type":"MatchesRegex"}).extend([make_node("Expression", "$$SYS:MCU_ID$$"), make_node("Regex", "|".join(cond))])
        flagsNode = ET.SubElement(flagCondNode, "Flags")
        includeDirListNode = ET.SubElement(flagsNode, "IncludeDirectories")
        includeDirListNode.append(make_node("string", dir))

    #if lib.ID == "ublox":
    #    add_file_condition(lib, fw, condList, "libraries/net/cellular/CellularModem/.*", "com.sysprogs.arm.mbed.ublox.CellularModem", "Cellular Modem Support")
    #    add_file_condition(lib, fw, condList, "libraries/net/cellular/CellularUSBModem/.*", "com.sysprogs.arm.mbed.ublox.CellularUSBModem", "USB Cellular Modem Support")
    #    add_file_condition(lib, fw, condList, "libraries/net/cellular/UbloxUSBModem/.*", "com.sysprogs.arm.mbed.ublox.UbloxUSBModem", "Ublox Cellular Modem Support")


samples = xml.find("Examples")
for (root,dirs,files) in os.walk(os.path.join(ROOT, "samples")):
    for subdir in dirs:
        samples.append(make_node("string", "samples/" + basename(subdir)))

xml.getroot().attrib["xmlns:xsi"] = "http://www.w3.org/2001/XMLSchema-instance";
xml.getroot().attrib["xmlns:xsd"] = "http://www.w3.org/2001/XMLSchema";
xml.write(join(ROOT, "BSP.xml"), xml_declaration=True,encoding='utf-8', method="xml")
