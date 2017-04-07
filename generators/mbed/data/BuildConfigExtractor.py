"""
    This file detects the build settings for all supported mbed targets and dumps them to an XML file readable by the BSP generator
"""
import copy
import json
import os
import re
import sys
import xml.etree.ElementTree as ElementTree
from copy import deepcopy
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

def macro_to_string(macro):
    if macro.macro_value:
        return macro.macro_name + '=' + macro.macro_value
    else:
        return macro.macro_name

class MemoryDefinition(object):
    def __init__(self, name, start_address, size):
        self.Name = name
        self.Start = start_address
        self.Size = size


class BuildConfiguration:
    def __init__(self, toolchain, resources, hexFiles = []):
        self.SourceFiles = resources.c_sources + resources.cpp_sources + resources.s_sources
        self.HeaderFiles = resources.headers + [MBED_HEADER]
        self.LinkerScript = resources.linker_script
        self.IncludeDirectories = resources.inc_dirs
        self.NormalPreprocessorMacros = toolchain.get_symbols()
        self.ConfigurationVariables, macros = toolchain.config.get_config_data()
        self.NormalPreprocessorMacros += [macro_to_string(v) for (k,v) in macros.items()]
        self.HexFiles = hexFiles


    def ToXML(self, nodeName):
        cfgNode = ElementTree.Element(nodeName)
        for (list, name) in [(self.SourceFiles, 'SourceFiles'),
                             (self.HeaderFiles, 'HeaderFiles'), 
                             (self.IncludeDirectories, 'IncludeDirectories'),
                             (self.NormalPreprocessorMacros, 'NormalPreprocessorMacros'),
                             (self.HexFiles, 'HexFiles')]:
            listNode = append_node(cfgNode, name)
            if list:
                [listNode.append(make_node('string', s)) for s in list]
        cfgNode.append(make_node('LinkerScript', self.LinkerScript))

        cfgVarListNode = append_node(cfgNode, 'ConfigurationVariables')
        for (key, cfgVar) in self.ConfigurationVariables.items():
            vn = append_node(cfgVarListNode, 'RawConfigurationVariable')
            vn.append(make_node('ID', cfgVar.name))
            vn.append(make_node('DefaultValue', str(cfgVar.value)))
            vn.append(make_node('Description', cfgVar.help_text))
            vn.append(make_node('MacroName', cfgVar.macro_name))
            vn.append(make_node('IsBool', str(cfgVar.is_bool)))
        return cfgNode         

script_path = join(dirname(__file__))

def main():
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

    supported_targets = Exporter.TARGETS
    #supported_targets = [t for t in Exporter.TARGETS if "NRF52_DK" in t or "NRF51_DK" in t]
    #supported_targets = [t for t in Exporter.TARGETS if "NRF5" in t]

    rootNode = ElementTree.Element('ParsedTargetList')
    targetListNode = append_node(rootNode, 'Targets')
    targetNumber = 1

    for target in supported_targets:
        print('\t' + target + ' (' + str(targetNumber) + '/' + str(len(supported_targets)) + ')...')
        targetNumber = targetNumber + 1
        targetNode = append_node(targetListNode, 'Target')
        targetNode.append(make_node('ID', target))

        toolchain = ba.prepare_toolchain([ROOT], "", target, 'GCC_ARM', silent=True)

        fullRes = toolchain.scan_resources(ROOT)
        fullRes.toolchain = toolchain

        hexFiles = bootloader_scanner.LocateHexFiles(toolchain, fullRes)

        #Find optional libraries (that have mbed_lib.json file in the root directory)
        mbed_library_dirs = [os.path.dirname(j) for j in fullRes.json_files if os.path.basename(j) == 'mbed_lib.json']

        #Treat the MBED platform library as a part of the SDK and exclude the TARGET_xxx subdirs inside library dirs
        mbed_library_dirs = [lib for lib in mbed_library_dirs if lib != os.path.join(ROOT, 'platform') and not os.path.basename(lib).startswith("TARGET_")]

        #Now rescan everything except the optional libraries under the 'features' directory
        minimal_res = toolchain.scan_resources(ROOT, exclude_paths = [os.path.join(ROOT, 'features')] + mbed_library_dirs)

        targetNode.append(make_node('Features', ";".join(toolchain.target.features)))
        toolchain.target.features = []
        toolchain.config.load_resources(minimal_res)

        baseCfg = BuildConfiguration(toolchain, minimal_res, hexFiles)
        targetNode.append(baseCfg.ToXML('BaseConfiguration'))
        targetNode.append(make_node('CFLAGS', ";".join(toolchain.cpu[:])))

        derivedCfgListNode = append_node(targetNode, 'DerivedConfigurations')

        for libDir in mbed_library_dirs:
            libNode = append_node(derivedCfgListNode, 'DerivedConfiguration')
            libNode.append(make_node('Library', libDir))

            libToolchain = deepcopy(toolchain)
            libRes = libToolchain.scan_resources(libDir)
            libToolchain.config.load_resources(libRes)
            libCfg = BuildConfiguration(libToolchain, libRes)
            libNode.append(libCfg.ToXML('Configuration'))

        for feature in fullRes.features:
            featureNode = append_node(derivedCfgListNode, 'DerivedConfiguration')
            featureNode.append(make_node('Feature', feature))

            featureToolchain = deepcopy(toolchain)
            featureRes = fullRes.features[feature]
            featureToolchain.config.load_resources(featureRes)
            featureCfg = BuildConfiguration(featureToolchain, featureRes)
            featureNode.append(featureCfg.ToXML('Configuration'))

        for lib in LIBRARIES:
            if lib['id'] in ['rtos' ,'rtx']:
                continue   #Already handled via mbed_library_dirs
            sourceDirs = lib['source_dir']
            if isinstance(sourceDirs, str):
                sourceDirs = [sourceDirs]

            libNode = append_node(derivedCfgListNode, 'DerivedConfiguration')
            libNode.append(make_node('Library', lib['id']))
            libNode.append(make_node('LibraryName', library_names.get(lib['id'])))

            cfgListNode = append_node(libNode, 'ConfigurationsToMerge')

            for srcDir in sourceDirs:
                libToolchain = deepcopy(toolchain)
                libRes = libToolchain.scan_resources(srcDir)
                libToolchain.config.load_resources(libRes)
                libCfg = BuildConfiguration(libToolchain, libRes)
                cfgListNode.append(libCfg.ToXML('BuildConfiguration'))

    rootNode.attrib['xmlns:xsi'] = 'http://www.w3.org/2001/XMLSchema-instance'
    rootNode.attrib['xmlns:xsd'] = 'http://www.w3.org/2001/XMLSchema'
    xml_str = ElementTree.tostring(rootNode)
    with open(join(ROOT, 'ParsedTargets.xml'), 'w') as xml_file:
        xml_file.write(xml_str.encode('utf-8'))

main()
