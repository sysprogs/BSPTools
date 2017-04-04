"""
    This file generates a BSP file that makes VisualGDB recognize the Mbed library and automatically create Visual Studio
    projects for it
"""
import copy
import json
import os
import re
import sys
import xml.etree.ElementTree as ElementTree
from subprocess import call;
from os.path import join, dirname, basename
from xml.dom import minidom

import tools.build_api as ba
import tools.config
import tools.libraries
import tools.project
import tools.toolchains
import tempfile;
from tools.export import EXPORTERS
from tools.libraries import LIBRARIES
from tools.paths import MBED_HEADER
from tools.settings import ROOT
import bootloader_scanner

# FILE_ROOT = abspath(join(dirname(tools.project.__file__), ".."))
# sys.path.insert(0, FILE_ROOT)


class LibraryBuilder(object):
    def __init__(self, lib, target):
        self.source_condition_map = {}
        self.header_condition_map = {}
        self.include_dir_condition_map = {}
        self.ID = lib['id']
        self.macros_condition_map = {}
        if lib.get('macros', None) is not None:
            self.append_resources(target, tools.toolchains.Resources(), lib.get('macros', None))
        self.SupportedTargets = {}
        self.Dependencies = lib['dependencies']

    def append_resources(self, target, res, macros):
        found = False
        for items, cond_map, is_path in [
            [res.c_sources + res.cpp_sources + res.s_sources, self.source_condition_map, True],
            [res.headers, self.header_condition_map, True],
            [res.inc_dirs, self.include_dir_condition_map, True],
            [macros, self.macros_condition_map, False]]:
                for fn in items:
                    if is_path:
                        fn = "$$SYS:BSP_ROOT$$/" + fn.replace("\\", "/")
                        found = True
                    cond_map.setdefault(fn, set([])).add(target)
        if found:
            self.SupportedTargets[target] = True


Exporter = EXPORTERS['gcc_arm']


def make_node(name, text):
    str_node = ElementTree.Element(name)
    str_node.text = text
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

def parse_linker_script(lds_file):
	emptyFile = tempfile.tempdir + "/0.c"
	open(emptyFile, 'a').close()
	mapFile = tempfile.tempdir + "/tmp.map"
	call(["arm-eabi-gcc", "-T" + lds_file.replace('\\', '/'), emptyFile.replace('\\', '/'), "-Wl,-Map," + mapFile.replace('\\', '/')])
	inSection = False
	result = []
	rgMemoryLine = re.compile("^([^ \t]+)[ \t]+0x([0-9a-fA-F]+)[ \t]+0x([0-9a-fA-F]+)");
	with open(mapFile) as f:
		for line in f.readlines():
			if "Memory Configuration" in line:
				inSection = True
			elif inSection:
				m = rgMemoryLine.search(line)
				if m:
				    name = m.group(1)
				    if name != "*default*":
				        result.append(MemoryDefinition(name, int(m.group(2),16), int(m.group(3), 16)))
				elif "Linker script and memory map" in line:
					break
	return result

	os.remove(emptyFile)
	os.remove(mapFile)


script_path = join(dirname(__file__))

def main():
    ignore_targets = {
        # Not compiled with the mbed-cli
        'ELEKTOR_COCORICO': 'Wrong target configuration, no \'device_has\' attribute',
        'KL26Z': 'undefined reference to \'init_data_bss\'',
        'LPC11U37_501': 'fatal error: device.h: No such file or directory',
        'LPC11U68': 'multiple definition of \'__aeabi_atexit\'',
        'SAMG55J19': 'error: \'s\' undeclared here: #define OPTIMIZE_HIGH __attribute__((optimize(s)))',
        'LPC810': 'region \'FLASH\' overflowed by 2832 bytes',
        'LPC2368': 'undefined reference to \'__get_PRIMASK\'',
        'LPC2460': 'undefined reference to \'__get_PRIMASK\'',
        'MTM_MTCONNECT04S_BOOT': 'fatal error: device.h: No such file or directory',
        'MTM_MTCONNECT04S_OTA': 'fatal error: device.h: No such file or directory',

        # Hex merge problem targets
        'NRF51_MICROBIT_BOOT': 'Hex file problem',
        'ARCH_BLE': 'Hex file problem',
        'RBLAB_NRF51822': 'Hex file problem',
        'RBLAB_BLENANO': 'Hex file problem',
        'NRF51822_BOOT': 'Hex file problem',
        'NRF51_MICROBIT': 'Hex file problem',
        'WALLBOT_BLE': 'Hex file problem',
        'WALLBOT_BLE_OTA': 'Hex file problem',
        'MTM_MTCONNECT04S': 'Hex file problem',
        'MTM_MTCONNECT04S_BOOT': 'Hex file problem',
        'TY51822R3_BOOT': 'Hex file problem',
        'NRF51822_OTA': 'Hex file problem',
        'RBLAB_NRF51822_OTA': 'Hex file problem',
        'NRF51822_Y5_MBUG': 'Hex file problem',
        'NRF51822': 'Hex file problem',
        'ARCH_BLE_BOOT': 'Hex file problem',
        'RBLAB_BLENANO_BOOT': 'Hex file problem',
        'TY51822R3_OTA': 'Hex file problem',
        'SEEED_TINY_BLE': 'Hex file problem',
        'RBLAB_NRF51822_BOOT': 'Hex file problem',
        'NRF51_DK_LEGACY': 'Hex file problem',
        'DELTA_DFCM_NNN40_OTA': 'Hex file problem',
        'TY51822R3': 'Hex file problem',
        'NRF51_DONGLE_LEGACY': 'Hex file problem',
        'DELTA_DFBM_NQ620': 'Hex file problem',
        'WALLBOT_BLE_BOOT': 'Hex file problem',
        'DELTA_DFCM_NNN40': 'Hex file problem',
        'SEEED_TINY_BLE_OTA': 'Hex file problem',
        'ARCH_LINK_OTA': 'Hex file problem',
        'NRF51_DK_BOOT': 'Hex file problem',
        'NRF51_DONGLE': 'Hex file problem',
        'DELTA_DFCM_NNN40_BOOT': 'Hex file problem',
        'NRF51_MICROBIT_B_OTA': 'Hex file problem',
        'NRF51_MICROBIT_B_BOOT': 'Hex file problem',
        'SEEED_TINY_BLE_BOOT': 'Hex file problem',
        'ARCH_LINK': 'Hex file problem',
        'NRF51_MICROBIT_B': 'Hex file problem',
        'NRF51_DK_OTA': 'Hex file problem',
        'RBLAB_BLENANO_OTA': 'Hex file problem',
        'ARCH_LINK_BOOT': 'Hex file problem',
        'ARCH_BLE_OTA': 'Hex file problem',
        'HRM1017': 'Hex file problem',
        'NRF52_DK': 'Hex file problem',
        'NRF51_DONGLE_OTA': 'Hex file problem',
        'NRF51_DONGLE_BOOT': 'Hex file problem',
        'NRF51_MICROBIT_OTA': 'Hex file problem',
        'NRF51_DK': 'Hex file problem',
        'HRM1017_BOOT': 'Hex file problem',
        'HRM1017_OTA': 'Hex file problem',

        # LED Blink problem targets
        'LPC1549': 'error: \'sleep\' was not declared in this scope',
        'NUMAKER_PFM_M453': 'multiple definition of \'__wrap__sbrk\'',
        'NUMAKER_PFM_NUC472': 'fatal error: mbedtls/config.h: No such file or directory',
        'RZ_A1H': 'error: \'sleep\' was not declared in this scope',
        'VK_RZ_A1H': 'error: \'sleep\' was not declared in this scope',

        # LED Blink RTOS problem targets
        'KL05Z': 'region \'RAM\' overflowed by 3020 bytes',
        'EFM32HG_STK3400': 'region RAM overflowed with stack',
        'VK_RZ_A1H': 'multiple definition of \'eth_arch_enetif_init\'',
        'LPC812': 'region \'RAM\' overflowed by 3108 bytes',
        'MAXWSNENV': 'undefined reference to *',
        'ARM_BEETLE_SOC': 'undefined reference to *',

        # USB Device problem targets
        'LPC1347': 'region \'RAM\' overflowed by 156 bytes',
        'MAX32620HSP': 'undefined reference to *',
        'EFM32HG_STK3400': ' region \'RAM\' overflowed by 516 bytes',
        'MAXWSNENV': 'undefined reference to *',
        'KL27Z': 'undefined reference to \'USBHAL\' + region \'m_data\' overflowed by 88 bytes',

    }

    with open(os.path.join(script_path, 'linker_data.json')) as linker_data:
        linker_data = json.load(linker_data)

    source_condition_map = {}
    header_condition_map = {}
    symbol_condition_map = {}
    include_dir_condition_map = {}
    src_dir_to_lib_map = {}
    resources_map = {}
    lib_builder_map = {}
    hexFileMap = open(join(ROOT, 'hexfiles.txt'), "w")

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
        'features': 'Device features'
    }

    print("Parsing targets...")
    xml = ElementTree.parse(os.path.join(script_path, 'bsp_template.xml'))
    mcus = xml.find("SupportedMCUs")
    family = xml.find("MCUFamilies/MCUFamily")

    targets_count = 0
    for target in Exporter.TARGETS:
        print('\t' + target + '...')

        toolchain = ba.prepare_toolchain(ROOT, "", target, 'GCC_ARM')

        # Scan src_path for config files
        res = toolchain.scan_resources(ROOT, exclude_paths=[os.path.join(ROOT, 'rtos'), os.path.join(ROOT, 'features')])
        res.toolchain = toolchain
        # for path in src_paths[1:]:
        #     resources.add(toolchain.scan_resources(path))

        hexFiles = bootloader_scanner.LocateHexFiles(toolchain, res)
        if hexFiles:
            hexFileMap.write(target + "\n")
            hexFileMap.writelines(["\t" + f + "\n" for f in hexFiles])
            hexFileMap.flush()

        res.headers += [MBED_HEADER, ROOT]
        # res += toolchain.scan_resources(os.path.join(ROOT, 'events'))

        toolchain.config.load_resources(res)

        target_lib_macros = toolchain.config.config_to_macros(toolchain.config.get_config_data())
        toolchain.set_config_data(toolchain.config.get_config_data())
        toolchain.config.validate_config()

        res.relative_to(ROOT, False)
        res.win_to_unix()

        for items, object_map, is_path in [
            [res.c_sources + res.cpp_sources + res.s_sources, source_condition_map, True],
            [res.headers, header_condition_map, True],
            [res.inc_dirs, include_dir_condition_map, True],
            [toolchain.get_symbols(), symbol_condition_map, False],
            [target_lib_macros, symbol_condition_map, False]]:
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
                lib_toolchain = ba.prepare_toolchain(ROOT, "", target, 'GCC_ARM')
                # ignore rtx while scanning rtos
                exclude_paths = [os.path.join(ROOT, 'rtos', 'rtx')] if lib['id'] != 'rtos' else []
                lib_res = lib_toolchain.scan_resources(src, exclude_paths=exclude_paths)
                lib_toolchain.config.load_resources(lib_res)
                lib_macros = lib_toolchain.config.config_to_macros(lib_toolchain.config.get_config_data())
                new_lib = copy.copy(lib)
                macros = new_lib.get('macros', None)
                if macros is None:
                    macros = lib_macros
                else:
                    macros += lib_macros
                new_lib['macros'] = macros
                lib_res.relative_to(ROOT, False)
                lib_res.win_to_unix()
                lib_builder_map.setdefault(new_lib['id'], LibraryBuilder(new_lib, target)).append_resources(
                    target, lib_res, macros)
                src_dir_to_lib_map[src] = new_lib['id']

        # Add specific features as a library
        features_path = os.path.join(ROOT, 'features')
        features_toolchain = ba.prepare_toolchain(features_path, "", target, 'GCC_ARM')
        features_resources = features_toolchain.scan_resources(features_path)
        features_toolchain.config.load_resources(features_resources)
        new_macros = features_toolchain.config.config_to_macros(features_toolchain.config.get_config_data())
        features_macros = [x for x in new_macros if x not in target_lib_macros]
        # if 'MBED_CONF_LWIP_ADDR_TIMEOUT=5' in features_macros:
        #     features_macros.remove('MBED_CONF_LWIP_ADDR_TIMEOUT=5')
        #     features_macros.append('MBED_CONF_LWIP_ADDR_TIMEOUT=$$com.sysprogs.bspoptions.lwip.addr_timeout$$')
        if 'MBED_CONF_LWIP_IPV6_ENABLED=0' in features_macros:
            features_macros.remove('MBED_CONF_LWIP_IPV6_ENABLED=0')
            features_macros.append('MBED_CONF_LWIP_IPV6_ENABLED=$$com.sysprogs.bspoptions.lwip.ipv6_en$$')
        if 'MBED_CONF_LWIP_IPV4_ENABLED=1' in features_macros:
            features_macros.remove('MBED_CONF_LWIP_IPV4_ENABLED=1')
            features_macros.append('MBED_CONF_LWIP_IPV4_ENABLED=$$com.sysprogs.bspoptions.lwip.ipv4_en$$')

        features_resources.relative_to(ROOT, False)
        features_resources.win_to_unix()
        features_lib = {
            'id': 'features',
            'source_dir': os.path.join(ROOT, 'features'),
            'build_dir': tools.libraries.RTOS_LIBRARIES,
            'dependencies': [tools.libraries.MBED_LIBRARIES, tools.libraries.MBED_RTX, tools.libraries.RTOS_LIBRARIES],
            'macros': features_macros
        }
        for feature in toolchain.config.get_features():
            if feature in features_resources.features:
                features_resources += features_resources.features[feature]
        lib_builder_map.setdefault('features', LibraryBuilder(features_lib, target)).append_resources(
            target, features_resources, features_macros)
        src_dir_to_lib_map[features_path] = 'features'

    for fw in lib_builder_map.values():
        fw.DependencyIDs = set([])
        for dep in fw.Dependencies:
            id = src_dir_to_lib_map.get(dep)
            if id is not None:
                fw.DependencyIDs.add(id)

    # Set flags different for each target
    include_ignored_targets = '--alltargets' in sys.argv
	
    for target in Exporter.TARGETS:
        res = resources_map.get(target, None)
        if res is None:
            print('Target ignored: ' + target + ': No resources')
            continue
        if res.linker_script is None:
            print('Target ignored: ' + target + ': No linker script')
            continue
        if not include_ignored_targets and target in ignore_targets:
            print('Target ' + target + ' ignored: ' + ignore_targets[target])
            continue

        mcu = ElementTree.Element('MCU')
        mcu.append(make_node('ID', target))
        mcu.append(make_node('HierarchicalPath', 'Mbed'))
        mcu.append(make_node('FamilyID', family.find('ID').text))

        props_list = provide_node(provide_node(provide_node(provide_node(mcu, "ConfigurableProperties"),
                                                            "PropertyGroups"), "PropertyGroup"), "Properties")

        if 'FEATURE_LWIP=1' in symbol_condition_map:
            if target in symbol_condition_map['FEATURE_LWIP=1']:
                prop_node = ElementTree.SubElement(props_list, "PropertyEntry", {"xsi:type": "Enumerated"})
                prop_node.extend([make_node('Name', 'LWIP IPV6 config'),
                                  make_node('UniqueID', 'com.sysprogs.bspoptions.lwip.ipv6_en'),
                                  make_node('DefaultEntryIndex', '1')])
                list_node = ElementTree.SubElement(prop_node, 'SuggestionList')
                ElementTree.SubElement(list_node, "Suggestion").extend([make_node("UserFriendlyName", "enable"),
                                                                        make_node("InternalValue", '1')])
                ElementTree.SubElement(list_node, "Suggestion").extend([make_node("UserFriendlyName", "disable"),
                                                                        make_node("InternalValue", '0')])

                prop_node = ElementTree.SubElement(props_list, "PropertyEntry", {"xsi:type": "Enumerated"})
                prop_node.extend([make_node("Name", "LWIP IPV4 config"),
                                  make_node("UniqueID", "com.sysprogs.bspoptions.lwip.ipv4_en"),
                                  make_node("DefaultEntryIndex", "0")])
                list_node = ElementTree.SubElement(prop_node, "SuggestionList")
                ElementTree.SubElement(list_node, "Suggestion").extend([make_node("UserFriendlyName", "enable"),
                                                                        make_node("InternalValue", '1')])
                ElementTree.SubElement(list_node, "Suggestion").extend([make_node("UserFriendlyName", "disable"),
                                                                        make_node("InternalValue", '0')])

        flags = append_node(mcu, "CompilationFlags")
        for (node, dict) in [[append_node(mcu, "AdditionalSourceFiles"), source_condition_map],
                             [append_node(mcu, "AdditionalHeaderFiles"), header_condition_map],
                             [append_node(flags, "IncludeDirectories"), include_dir_condition_map],
                             [append_node(flags, "PreprocessorMacros"), symbol_condition_map]]:
            for (filename, targets) in dict.items():
                if len(list(set(targets))) < targets_count and target in targets:
                    node.append(make_node("string", filename))

        flagList = res.toolchain.cpu[:]
        if "-mfloat-abi=softfp" in flagList:
            flagList.remove("-mfloat-abi=softfp")
            flagList.append("$$com.sysprogs.bspoptions.arm.floatmode$$")
            prop_node = ElementTree.SubElement(props_list, "PropertyEntry", {"xsi:type": "Enumerated"})
            prop_node.extend([make_node("Name", "Floating point support"),
                           make_node("UniqueID", "com.sysprogs.bspoptions.arm.floatmode"),
                           make_node("DefaultEntryIndex", "2")])
            list_node = ElementTree.SubElement(prop_node, "SuggestionList")
            ElementTree.SubElement(list_node, "Suggestion").extend(
                [make_node("UserFriendlyName", "Software"), make_node("InternalValue", "-mfloat-abi=soft")])
            ElementTree.SubElement(list_node, "Suggestion").extend(
                [make_node("UserFriendlyName", "Hardware"), make_node("InternalValue", "-mfloat-abi=hard")])
            ElementTree.SubElement(list_node, "Suggestion").extend(
                [make_node("UserFriendlyName", "Hardware with Software interface"),
                 make_node("InternalValue", "-mfloat-abi=softfp")])
            ElementTree.SubElement(list_node, "Suggestion").extend(
                [make_node("UserFriendlyName", "Unspecified"), make_node("InternalValue", "")])

        ElementTree.SubElement(flags, "COMMONFLAGS").text = " ".join(flagList)
        ElementTree.SubElement(flags, "LinkerScript").text = "$$SYS:BSP_ROOT$$/" + res.linker_script

        mems = parse_linker_script(os.path.join(ROOT, res.linker_script))
        ram_size = str(sum([m.Size for m in mems if ("RAM" in m.Name.upper())]))
        flash_size = str(sum([m.Size for m in mems if ("FLASH" in m.Name.upper())]))
        if target in linker_data:
            ram_size = linker_data[target]['RAM']
            flash_size = linker_data[target]['FLASH']
        else:
            print('No RAM and FLASH size for a target ' + target)
        mcu.append(make_node("RAMSize", ram_size))
        mcu.append(make_node("FLASHSize", flash_size))

        mem_list = ElementTree.SubElement(ElementTree.SubElement(mcu, "MemoryMap"), "Memories")
        for mem in mems:
            mem_el = ElementTree.SubElement(mem_list, "MCUMemory")
            mem_el.append(make_node("Name", mem.Name))
            mem_el.append(make_node("Address", str(mem.Start)))
            mem_el.append(make_node("Size", str(mem.Size)))
            if mem.Name.upper() == "FLASH":
                mem_el.append(make_node("Flags", "IsDefaultFLASH"))
            if mem.Name.upper() == "RAM":
                mem_el.append(make_node("LoadedFromMemory", "FLASH"))

        mcus.append(mcu)

    # Set flags shared between targets
    flags = append_node(family, "CompilationFlags")
    for (node, dict) in [[append_node(family, "AdditionalSourceFiles"), source_condition_map],
                         [append_node(family, "AdditionalHeaderFiles"), header_condition_map],
                         [append_node(flags, "IncludeDirectories"), include_dir_condition_map],
                         [append_node(flags, "PreprocessorMacros"), symbol_condition_map]]:
        for (filename, targets) in dict.items():
            if len(list(set(targets))) == targets_count:
                node.append(make_node("string", filename))

    family.find("AdditionalSourceFiles").append(make_node("string", "$$SYS:BSP_ROOT$$/stubs.cpp"))
    cond_list = xml.find("FileConditions")
    flag_cond_list = xml.find("ConditionalFlags")

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
        ElementTree.SubElement(fw, "AdditionalPreprocessorMacros").extend(
            [make_node("string", fn) for fn in lib.macros_condition_map.keys()])
        if len(lib.DependencyIDs) > 0:
            ElementTree.SubElement(fw, "RequiredFrameworks").extend(
                [make_node("string", "com.sysprogs.arm.mbed." + id) for id in lib.DependencyIDs])
        # ET.SubElement(ET.SubElement(fw, "AdditionalSystemVars"), "SysVarEntry").extend([make_node("Key", "com.sysprogs.arm.mbed." + lib.ID + ".included"), make_node("Value", "1")])

        for (fn, cond) in lib.source_condition_map.items() + lib.header_condition_map.items():
            if len(cond) == len(lib.SupportedTargets):
                continue
            if len(cond) > len(lib.SupportedTargets):
                raise AssertionError("Source file condition list longer than the framework condition list. "
                                     "Check how the framework conditions are formed.")
            file_cond_node = ElementTree.SubElement(cond_list, "FileCondition")
            h_cond_node = ElementTree.SubElement(file_cond_node, "ConditionToInclude", {"xsi:type": "MatchesRegex"})
            h_cond_node.append(make_node("Expression", "$$SYS:MCU_ID$$"))
            h_cond_node.append(make_node("Regex", "|".join(cond)))
            file_cond_node.append(make_node("FilePath", fn))

        for (inc_dir, cond) in lib.include_dir_condition_map.items():
            if len(cond) == len(lib.SupportedTargets):
                continue
            if len(cond) > len(lib.SupportedTargets):
                raise AssertionError("Source file condition list longer than the framework condition list. "
                                     "Check how the framework conditions are formed.")
            flag_cond_node = ElementTree.SubElement(flag_cond_list, "ConditionalToolFlags")
            cond_list_node = ElementTree.SubElement(
                ElementTree.SubElement(flag_cond_node, "FlagCondition", {"xsi:type": "And"}), "Arguments")
            ElementTree.SubElement(cond_list_node, "Condition", {"xsi:type": "ReferencesFramework"}).append(
                make_node("FrameworkID", "com.sysprogs.arm.mbed." + lib.ID))
            ElementTree.SubElement(cond_list_node, "Condition", {"xsi:type": "MatchesRegex"}).extend(
                [make_node("Expression", "$$SYS:MCU_ID$$"), make_node("Regex", "|".join(cond))])
            flags_node = ElementTree.SubElement(flag_cond_node, "Flags")
            include_dir_list_node = ElementTree.SubElement(flags_node, "IncludeDirectories")
            include_dir_list_node.append(make_node("string", inc_dir))

        for (macro, cond) in lib.macros_condition_map.items():
            if len(cond) == len(lib.SupportedTargets):
                continue
            if len(cond) > len(lib.SupportedTargets):
                raise AssertionError('A number of macros is larger than number of supported targets')
            macro_cond_node = ElementTree.SubElement(flag_cond_list, "ConditionalToolFlags")
            macro_list_node = ElementTree.SubElement(
                ElementTree.SubElement(macro_cond_node, "FlagCondition", {"xsi:type": "And"}), "Arguments")
            ElementTree.SubElement(macro_list_node, "Condition", {"xsi:type": "ReferencesFramework"}).append(
                make_node("FrameworkID", "com.sysprogs.arm.mbed." + lib.ID))
            ElementTree.SubElement(macro_list_node, "Condition", {"xsi:type": "MatchesRegex"}).extend(
                [make_node("Expression", "$$SYS:MCU_ID$$"), make_node("Regex", "|".join(cond))])
            macro_flags_node = ElementTree.SubElement(macro_cond_node, 'Flags')
            macros_node = ElementTree.SubElement(macro_flags_node, 'PreprocessorMacros')
            macros_node.append(make_node('string', macro))

    samples = xml.find('Examples')
    for (root, dirs, files) in os.walk(os.path.join(ROOT, 'samples')):
        for subdir in dirs:
            samples.append(make_node('string', 'samples/' + basename(subdir)))

    xml.getroot().attrib['xmlns:xsi'] = 'http://www.w3.org/2001/XMLSchema-instance'
    xml.getroot().attrib['xmlns:xsd'] = 'http://www.w3.org/2001/XMLSchema'
    root_node = minidom.parseString(ElementTree.tostring(xml.getroot()))
    xml_str = '\n'.join([line for line in root_node.toprettyxml(indent=' '*2).split('\n') if line.strip()])
    with open(join(ROOT, 'BSP.xml'), 'w') as xml_file:
        xml_file.write(xml_str.encode('utf-8'))



main()
