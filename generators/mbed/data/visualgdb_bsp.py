"""
    This file generates a BSP file that makes VisualGDB recognize the Mbed library and automatically create Visual Studio
    projects for it
"""
import sys
import os
import re
import xml.etree.ElementTree as ElementTree
import tools.project
import json
import tools.build_api as ba
import tools.config

from os.path import join, abspath, dirname, basename
from tools.settings import ROOT
from xml.dom import minidom
# from tools.paths import MBED_BASE
from tools.export import EXPORTERS
from tools.libraries import LIBRARIES
from tools.targets import TARGET_MAP
from tools.toolchains import TOOLCHAIN_CLASSES
from tools.paths import MBED_DRIVERS, MBED_PLATFORM, MBED_HAL, MBED_HEADER, MBED_CMSIS_PATH, MBED_TARGETS_PATH

FILE_ROOT = abspath(join(dirname(tools.project.__file__), ".."))
sys.path.insert(0, FILE_ROOT)


class LibraryBuilder(object):
    def __init__(self, lib):
        self.source_condition_map = {}
        self.header_condition_map = {}
        self.include_dir_condition_map = {}
        self.ID = lib['id']
        self.Macros = lib.get('macros', None)
        self.SupportedTargets = {}
        self.Dependencies = lib['dependencies']

    def append_resources(self, target, res):
        found = False
        for items, cond_map, is_path in [
                [res.c_sources + res.cpp_sources + res.s_sources, self.source_condition_map, True],
                [res.headers, self.header_condition_map, True],
                [res.inc_dirs, self.include_dir_condition_map, True]]:
                for fn in items:
                    if is_path:
                        fn = "$$SYS:BSP_ROOT$$/" + fn.replace("\\", "/")
                        found = True
                    cond_map.setdefault(fn, set([])).add(target)
        if found:
            self.SupportedTargets[target] = True


Exporter = EXPORTERS['gcc_arm']


def scan_dir_contents(path, _toolchain, exclude_paths=None):
    res = _toolchain.scan_resources(path, exclude_paths=exclude_paths)
    res.relative_to(ROOT, False)
    res.win_to_unix()
    return res


def make_node(name, text):
    str_node = ElementTree.Element(name)
    str_node.text = text
    str_node.tail = "\n"
    return str_node


def append_node(el, name):
    n = ElementTree.Element(name)
    el.append(n)
    return n


def provide_node(el, name):
    n = el.find(name)
    if n is None:
        n = ElementTree.Element(name)
        el.append(n)
    return n


class MemoryDefinition(object):
    def __init__(self, name, start_address, size):
        self.Name = name
        self.Start = start_address
        self.Size = size


def add_file_condition(lib_builder, fw_node, cond_list, file_regex, condition_id, condition_name):
    prop_list = provide_node(
        provide_node(provide_node(provide_node(fw_node, "ConfigurableProperties"), "PropertyGroups"), "PropertyGroup"),
        "Properties")
    ElementTree.SubElement(prop_list, "PropertyEntry", {"xsi:type": "Boolean"}).extend(
        [make_node("Name", condition_name), make_node("UniqueID", condition_id), make_node("ValueForTrue", "1")])
    pattern = re.compile(file_regex)

    for s in lib_builder.sourceConditionMap.keys() + lib_builder.header_condition_map.keys():
        if not pattern.match(s.replace("$$SYS:BSP_ROOT$$/", "")):
            continue
        file_condition_node = ElementTree.SubElement(cond_list, "FileCondition")
        condition_node = ElementTree.SubElement(file_condition_node, "ConditionToInclude", {"xsi:type": "Equals"})
        condition_node.append(make_node("Expression", "$$" + condition_id + "$$"))
        condition_node.append(make_node("ExpectedValue", "1"))
        file_condition_node.append(make_node("FilePath", s))


def parse_mem_size(size_str):
    size_reg_ex = re.compile("[( ]*([0-9a-fA-FxKkMm]+) *([+-]) *([0-9a-fA-FxKkMm]+)[) ]*")
    size_str = size_str.strip('()\n')

    match = size_reg_ex.match(size_str)
    if match is not None:
        if match.group(2) == '+':
            return parse_mem_size(match.group(1)) + parse_mem_size(match.group(3))
        else:
            return parse_mem_size(match.group(1)) - parse_mem_size(match.group(3))

    multiplier = 1
    if size_str[-1].upper() == 'K':
        multiplier = 1024
        size_str = size_str[:-1]
    elif size_str[-1].upper() == 'M':
        multiplier = 1024 * 1024
        size_str = size_str[:-1]

    if size_str[0:2] == "0x":
        return int(size_str, 16) * multiplier
    else:
        return int(size_str, 10) * multiplier


def parse_linker_script(lds_file):
    inside_memory_block = False
    rg_mem_def = re.compile(' *([^ ]+) *\([^()]+\) *: *ORIGIN *= *([^ ,]+), *LENGTH *= *([^/]*)($|/\\*)')

    result = []
    with open(lds_file) as f:
        for line in f:
            if not inside_memory_block and line.strip() == "MEMORY":
                inside_memory_block = True
            elif inside_memory_block and line.strip() == "}":
                break
            elif inside_memory_block:
                match = rg_mem_def.match(line)
                if match is not None:
                    mem = MemoryDefinition(match.group(1), parse_mem_size(match.group(2)),
                                           parse_mem_size(match.group(3)))
                    if mem.Size != 0:
                        result.append(mem)
    return result


def main():
    ignore_targets = {
        # 'K22F': 'Test OK, FLASH and ROM size can\'t be parsed from a linker script',
        # 'K64F': 'Test OK, FLASH and ROM size can\'t be parsed from a linker script',
        # 'KL43Z': 'Test OK, FLASH and ROM size can\'t be parsed from a linker script',

        # 'LPC4330_M4': 'Test OK, FLASH and ROM size can\'t be parsed from a linker script',
        # 'SAMR21G18A': 'Test OK, FLASH and ROM size can\'t be parsed from a linker script',
        # 'SAML21J18A': 'Test OK, FLASH and ROM size can\'t be parsed from a linker script',
        # 'SAMG55J19': 'Test OK, FLASH and ROM size can\'t be parsed from a linker script',
        # 'SAMD21J18A': 'Test OK, FLASH and ROM size can\'t be parsed from a linker script',
        # 'SAMD21G18A': 'Test OK, FLASH and ROM size can\'t be parsed from a linker script',
        # 'MTS_GAMBIT': 'Test OK, FLASH and ROM size can\'t be parsed from a linker script',
        # 'LPC4330_M4': 'Test OK, FLASH and ROM size can\'t be parsed from a linker script',

        # 'KL05Z': 'Test failed: not fitted in FLASH region',

        # 'LPC11U24': 'Test failed: not fitted in FLASH region',
        # 'LPC812': 'Test failed: not fitted in FLASH region',
        # 'LPC2368': 'Test failed: undefined reference to __get_PRIMASK',
        # 'LPC2460': 'Test failed: undefined reference to __get_PRIMASK',
        # 'NUCLEO_F031K6': 'Test failed: not fitted in FLASH region',
        # 'EFM32ZG_STK3200': 'Test failed: not fitted in FLASH region',
        # 'NUCLEO_F042K6': 'Test failed: not fitted in FLASH region',
        # 'NUCLEO_L011K4': 'Test failed: not fitted in FLASH region',
        # 'SAMG55J19': 'Test failed:\'osc_wait_ready\' was not declared in this scope',
    }
    # additional_defines = {}
    # with open(os.path.join(ROOT, 'additional_defines.json')) as j:
    #     additional_defines = json.loads(j.read())

    source_condition_map = {}
    header_condition_map = {}
    symbol_condition_map = {}
    include_dir_condition_map = {}
    src_dir_to_lib_map = {}
    resources_map = {}
    lib_builder_map = {}

    library_names = {
        'cpputest': "CppUTest",
        'usb_host': "USB Host support",
        'usb': "USB Device support",
        'ublox': "U-blox drivers",
        'rtos': "RTOS abstraction layer",
        'dsp': "DSP Library",
        'rpc': "RPC Support",
        'fat': "FAT File System support",
        'eth': "Ethernet support",
        'rtx': "Keil RTX RTOS",
    }

    # Add O_BINARY  definition
    o_bin_file_lines = []
    with open(os.path.join(ROOT, 'platform', 'retarget.cpp'), 'r') as r:
        o_bin_def = '#ifndef O_BINARY\n#define O_BINARY 0x10000\n#endif\n\n'
        pattern = re.compile('^#if defined.*')
        o_bin_file_lines = r.readlines()
        for i, l in enumerate(o_bin_file_lines):
            if pattern.match(l):
                o_bin_file_lines.insert(i, o_bin_def)
                break
    with open(os.path.join(ROOT, 'platform', 'retarget.cpp'), 'w+') as r:
        r.writelines(o_bin_file_lines)

    print("Parsing targets...")
    xml = ElementTree.parse(join(dirname(__file__), 'bsp_template.xml'))
    mcus = xml.find("SupportedMCUs")
    family = xml.find("MCUFamilies/MCUFamily")
    exclude_paths = [d for d in os.listdir(ROOT) if os.path.isdir(os.path.join(ROOT, d))]
    search_paths = ['targets', 'events', 'drivers']
    for p in search_paths:
        if p in exclude_paths:
            exclude_paths.remove(p)

    targets_count = 0
    for target in Exporter.TARGETS:
        print('\t' + target + '...')
        # toolchain = TOOLCHAIN_CLASSES['GCC_ARM'](TARGET_MAP[target.upper()])
        # config = tools.config.Config(target)
        # toolchain.config = config
        #
        # res = scan_dir_contents(MBED_DRIVERS, toolchain)
        # res.toolchain = toolchain
        # for d in [MBED_PLATFORM, MBED_HAL, MBED_CMSIS_PATH, MBED_TARGETS_PATH, os.path.join(ROOT, 'features'), os.path.join(ROOT, 'events')]:
        #     res += scan_dir_contents(d, toolchain)
        # res.headers += [os.path.relpath(MBED_HEADER, ROOT)]
        # res.inc_dirs += ['./']

        toolchain = ba.prepare_toolchain(ROOT, target, 'GCC_ARM')

        # Scan src_path for config files
        res = toolchain.scan_resources(ROOT, exclude_paths=[os.path.join(ROOT, 'rtos'), os.path.join(ROOT, 'features')])
        res.toolchain = toolchain
        # for path in src_paths[1:]:
        #     resources.add(toolchain.scan_resources(path))

        # Add features while we find new ones
        features = toolchain.config.get_features()
        for feature in features:
            if feature in res.features:
                res += res.features[feature]
        res.headers += [MBED_HEADER, ROOT]
        res.inc_dirs += ['./']
        res += toolchain.scan_resources(os.path.join(ROOT, 'events'))

        toolchain.config.load_resources(res)

        scfg = toolchain.config.get_config_data()
        toolchain.set_config_data(toolchain.config.get_config_data())

        toolchain.config.validate_config()

        cfg, macros = toolchain.config.get_config_data()
        features = toolchain.config.get_features()
        res.relative_to(ROOT, False)
        res.win_to_unix()



        # a, b, c = ba.get_config(ROOT, target, 'GCC_ARM')
        # if len(c) != 0:
        #     print('a')

        syms = toolchain.get_symbols()

        for items, object_map, is_path in [
            [res.c_sources + res.cpp_sources + res.s_sources, source_condition_map, True],
            [res.headers, header_condition_map, True],
            [res.inc_dirs, include_dir_condition_map, True],
            [toolchain.get_symbols(), symbol_condition_map, False],
            [['MBED_CONF_PLATFORM_DEFAULT_SERIAL_BAUD_RATE=9600'], symbol_condition_map, False]]:
            for fn in items:
                if is_path:
                    fn = "$$SYS:BSP_ROOT$$/" + fn.replace("\\", "/")
                object_map.setdefault(fn, []).append(target)
        targets_count += 1
        resources_map[target] = res

        for lib in LIBRARIES:
            sources = lib['source_dir']
            if isinstance(sources, str):
                sources = [sources]
            for src in sources:
                lib_resources = toolchain.scan_resources(src)
                lib_resources.relative_to(ROOT, False)
                lib_resources.win_to_unix()
                lib_builder_map.setdefault(lib['id'], LibraryBuilder(lib)).append_resources(target, lib_resources)
                src_dir_to_lib_map[src] = lib['id']

    for fw in lib_builder_map.values():
        fw.DependencyIDs = set([])
        for dep in fw.Dependencies:
            id = src_dir_to_lib_map.get(dep)
            if id is not None:
                fw.DependencyIDs.add(id)

    # Set flags different for each target
    for target in Exporter.TARGETS:
        mcu = ElementTree.Element("MCU")
        mcu.append(make_node("ID", target))
        mcu.append(make_node("HierarchicalPath", "Mbed"))
        mcu.append(make_node("FamilyID", family.find("ID").text))
        flags = append_node(mcu, "CompilationFlags")
        for (node, dict) in [[append_node(mcu, "AdditionalSourceFiles"), source_condition_map],
                             [append_node(mcu, "AdditionalHeaderFiles"), header_condition_map],
                             [append_node(flags, "IncludeDirectories"), include_dir_condition_map],
                             [append_node(flags, "PreprocessorMacros"), symbol_condition_map]]:
            for (filename, targets) in dict.items():
                if len(targets) < targets_count and target in targets:
                    node.append(make_node("string", filename))

        res = resources_map.get(target, None)
        if res is None:
            print('Target ignored: ' + target + ': No resources')
            continue
        if res.linker_script is None:
            print('Target ignored: ' + target + ': No linker script')
            continue
        if target in ignore_targets:
            print('Target ' + target + ' ignored: ' + ignore_targets[target])
            continue

        flagList = res.toolchain.cpu[:]
        if "-mfloat-abi=softfp" in flagList:
            flagList.remove("-mfloat-abi=softfp")
            flagList.append("$$com.sysprogs.bspoptions.arm.floatmode$$")
            propList = provide_node(
                provide_node(provide_node(provide_node(mcu, "ConfigurableProperties"), "PropertyGroups"),
                             "PropertyGroup"), "Properties")
            propEl = ElementTree.SubElement(propList, "PropertyEntry", {"xsi:type": "Enumerated"})
            propEl.extend([make_node("Name", "Floating point support"),
                           make_node("UniqueID", "com.sysprogs.bspoptions.arm.floatmode"),
                           make_node("DefaultEntryIndex", "2")])
            listEl = ElementTree.SubElement(propEl, "SuggestionList")
            ElementTree.SubElement(listEl, "Suggestion").extend(
                [make_node("UserFriendlyName", "Software"), make_node("InternalValue", "-mfloat-abi=soft")])
            ElementTree.SubElement(listEl, "Suggestion").extend(
                [make_node("UserFriendlyName", "Hardware"), make_node("InternalValue", "-mfloat-abi=hard")])
            ElementTree.SubElement(listEl, "Suggestion").extend(
                [make_node("UserFriendlyName", "Hardware with Software interface"),
                 make_node("InternalValue", "-mfloat-abi=softfp")])
            ElementTree.SubElement(listEl, "Suggestion").extend(
                [make_node("UserFriendlyName", "Unspecified"), make_node("InternalValue", "")])

        ElementTree.SubElement(flags, "COMMONFLAGS").text = " ".join(flagList)
        ElementTree.SubElement(flags, "LinkerScript").text = "$$SYS:BSP_ROOT$$/" + res.linker_script

        mems = parse_linker_script(os.path.join(ROOT, res.linker_script))
        mcu.append(make_node("RAMSize", str(sum([m.Size for m in mems if ("RAM" in m.Name.upper())]))))
        mcu.append(make_node("FLASHSize", str(sum([m.Size for m in mems if ("FLASH" in m.Name.upper())]))))

        memList = ElementTree.SubElement(ElementTree.SubElement(mcu, "MemoryMap"), "Memories")
        for mem in mems:
            memEl = ElementTree.SubElement(memList, "MCUMemory")
            memEl.append(make_node("Name", mem.Name))
            memEl.append(make_node("Address", str(mem.Start)))
            memEl.append(make_node("Size", str(mem.Size)))
            if mem.Name.upper() == "FLASH":
                memEl.append(make_node("Flags", "IsDefaultFLASH"))
            if mem.Name.upper() == "RAM":
                memEl.append(make_node("LoadedFromMemory", "FLASH"))

        mcus.append(mcu)

    # Set flags shared between targets
    flags = append_node(family, "CompilationFlags")
    for (node, dict) in [[append_node(family, "AdditionalSourceFiles"), source_condition_map],
                         [append_node(family, "AdditionalHeaderFiles"), header_condition_map],
                         [append_node(flags, "IncludeDirectories"), include_dir_condition_map],
                         [append_node(flags, "PreprocessorMacros"), symbol_condition_map]]:
        for (filename, targets) in dict.items():
            if len(targets) == targets_count:
                node.append(make_node("string", filename))

    family.find("AdditionalSourceFiles").append(make_node("string", "$$SYS:BSP_ROOT$$/stubs.cpp"))
    condList = xml.find("FileConditions")
    flagCondList = xml.find("ConditionalFlags")

    # Add frameworks
    for lib in lib_builder_map.values():
        fw = ElementTree.SubElement(xml.find("Frameworks"), "EmbeddedFramework")
        if len(lib.SupportedTargets) != targets_count:
            fw.append(make_node("MCUFilterRegex", "|".join(lib.SupportedTargets.keys())))

        fw.append(make_node("ID", "com.sysprogs.arm.mbed." + lib.ID))
        fw.append(make_node("ProjectFolderName", lib.ID))
        fw.append(make_node("UserFriendlyName", library_names.get(lib.ID, lib.ID + " library")))
        ElementTree.SubElement(fw, "AdditionalSourceFiles").extend(
            [make_node("string", fn) for fn in lib.source_condition_map.keys()])
        ElementTree.SubElement(fw, "AdditionalHeaderFiles").extend(
            [make_node("string", fn) for fn in lib.header_condition_map.keys()])
        ElementTree.SubElement(fw, "AdditionalIncludeDirs").extend(
            [make_node("string", fn) for (fn, cond) in lib.include_dir_condition_map.items() if
             len(cond) == len(lib.SupportedTargets)])
        if len(lib.DependencyIDs) > 0:
            ElementTree.SubElement(fw, "RequiredFrameworks").extend(
                [make_node("string", "com.sysprogs.arm.mbed." + id) for id in lib.DependencyIDs])
        # ET.SubElement(ET.SubElement(fw, "AdditionalSystemVars"), "SysVarEntry").extend([make_node("Key", "com.sysprogs.arm.mbed." + lib.ID + ".included"), make_node("Value", "1")])
        if lib.Macros is not None:
            ElementTree.SubElement(fw, "AdditionalPreprocessorMacros").extend(
                [make_node("string", m) for m in lib.Macros])

        for (fn, cond) in lib.source_condition_map.items() + lib.header_condition_map.items():
            if len(cond) == len(lib.SupportedTargets):
                continue
            if len(cond) > len(lib.SupportedTargets):
                raise AssertionError("Source file condition list longer than the framework condition list. "
                                     "Check how the framework conditions are formed.")
            fileCondNode = ElementTree.SubElement(condList, "FileCondition")
            condNode = ElementTree.SubElement(fileCondNode, "ConditionToInclude", {"xsi:type": "MatchesRegex"})
            condNode.append(make_node("Expression", "$$SYS:MCU_ID$$"))
            condNode.append(make_node("Regex", "|".join(cond)))
            fileCondNode.append(make_node("FilePath", fn))

        for (dir, cond) in lib.include_dir_condition_map.items():
            if len(cond) == len(lib.SupportedTargets):
                continue
            if len(cond) > len(lib.SupportedTargets):
                raise AssertionError("Source file condition list longer than the framework condition list. "
                                     "Check how the framework conditions are formed.")
            flagCondNode = ElementTree.SubElement(flagCondList, "ConditionalToolFlags")
            condListNode = ElementTree.SubElement(
                ElementTree.SubElement(flagCondNode, "FlagCondition", {"xsi:type": "And"}), "Arguments")
            ElementTree.SubElement(condListNode, "Condition", {"xsi:type": "ReferencesFramework"}).append(
                make_node("FrameworkID", "com.sysprogs.arm.mbed." + lib.ID))
            ElementTree.SubElement(condListNode, "Condition", {"xsi:type": "MatchesRegex"}).extend(
                [make_node("Expression", "$$SYS:MCU_ID$$"), make_node("Regex", "|".join(cond))])
            flagsNode = ElementTree.SubElement(flagCondNode, "Flags")
            includeDirListNode = ElementTree.SubElement(flagsNode, "IncludeDirectories")
            includeDirListNode.append(make_node("string", dir))

            # if lib.ID == "ublox":
            #    add_file_condition(lib, fw, condList, "libraries/net/cellular/CellularModem/.*", "com.sysprogs.arm.mbed.ublox.CellularModem", "Cellular Modem Support")
            #    add_file_condition(lib, fw, condList, "libraries/net/cellular/CellularUSBModem/.*", "com.sysprogs.arm.mbed.ublox.CellularUSBModem", "USB Cellular Modem Support")
            #    add_file_condition(lib, fw, condList, "libraries/net/cellular/UbloxUSBModem/.*", "com.sysprogs.arm.mbed.ublox.UbloxUSBModem", "Ublox Cellular Modem Support")

    samples = xml.find("Examples")
    for (root, dirs, files) in os.walk(os.path.join(ROOT, "samples")):
        for subdir in dirs:
            samples.append(make_node("string", "samples/" + basename(subdir)))

    xml.getroot().attrib["xmlns:xsi"] = "http://www.w3.org/2001/XMLSchema-instance"
    xml.getroot().attrib["xmlns:xsd"] = "http://www.w3.org/2001/XMLSchema"
    root_node = minidom.parseString(ElementTree.tostring(xml.getroot()))
    xml_str = '\n'.join([line for line in root_node.toprettyxml(indent=' '*2).split('\n') if line.strip()])
    with open(join(FILE_ROOT, "BSP.xml"), "w") as xml_file:
        xml_file.write(xml_str.encode('utf-8'))


main()
