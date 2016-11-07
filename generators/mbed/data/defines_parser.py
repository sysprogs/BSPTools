import subprocess as sp
import sys
import os
import re
import json

# TODO: Add mbed import command
# TODO: Integrate into BSP Generator
targets = [
    'ARCH_BLE', 'ARCH_BLE_BOOT', 'ARCH_BLE_OTA', 'ARCH_GPRS', 'ARCH_LINK', 'ARCH_LINK_BOOT', 'ARCH_LINK_OTA',
    'ARCH_MAX', 'ARCH_PRO', 'ARM_BEETLE_SOC', 'ARM_IOTSS_BEID', 'ARM_MPS2_M0', 'ARM_MPS2_M0P', 'ARM_MPS2_M1',
    'ARM_MPS2_M3', 'ARM_MPS2_M4', 'ARM_MPS2_M7', 'B96B_F446VE', 'BLUEPILL_F103C8', 'DELTA_DFBM_NQ620',
    'DELTA_DFCM_NNN40', 'DELTA_DFCM_NNN40_BOOT', 'DELTA_DFCM_NNN40_OTA', 'DISCO_F051R8', 'DISCO_F100RB', 'DISCO_F303VC',
    'DISCO_F334C8', 'DISCO_F401VC', 'DISCO_F407VG', 'DISCO_F429ZI', 'DISCO_F469NI', 'DISCO_F746NG', 'DISCO_F769NI',
    'DISCO_L053C8', 'DISCO_L476VG', 'EFM32GG_STK3700', 'EFM32HG_STK3400', 'EFM32LG_STK3600', 'EFM32PG_STK3401',
    'EFM32WG_STK3800', 'EFM32ZG_STK3200', 'ELEKTOR_COCORICO', 'ELMO_F411RE', 'HEXIWEAR', 'HRM1017', 'HRM1017_BOOT',
    'HRM1017_OTA', 'K20D50M', 'K22F', 'K64F', 'K66F', 'KL05Z', 'KL25Z', 'KL26Z', 'KL27Z', 'KL43Z', 'KL46Z', 'KL82Z',
    'LPC1114', 'LPC11C24', 'LPC11U24', 'LPC11U24_301', 'LPC11U34_421', 'LPC11U35_401', 'LPC11U35_501',
    'LPC11U35_501_IBDAP', 'LPC11U35_Y5_MBUG', 'LPC11U37H_401', 'LPC11U37_501', 'LPC11U68', 'LPC1347',
    'LPC1549', 'LPC1768', 'LPC2368', 'LPC2460', 'LPC4088', 'LPC4088_DM', 'LPC4330_M0', 'LPC4330_M4', 'LPC4337',
    'LPC810', 'LPC812', 'LPC824', 'LPCCAPPUCCINO', 'MAX32600MBED', 'MAX32620HSP', 'MAXWSNENV', 'MICRONFCBOARD',
    'MOTE_L152RC', 'MTM_MTCONNECT04S', 'MTM_MTCONNECT04S_BOOT', 'MTM_MTCONNECT04S_OTA', 'MTS_DRAGONFLY_F411RE',
    'MTS_GAMBIT', 'MTS_MDOT_F405RG', 'MTS_MDOT_F411RE', 'NCS36510', 'NRF51822', 'NRF51822_BOOT', 'NRF51822_OTA',
    'NRF51822_Y5_MBUG', 'NRF51_DK', 'NRF51_DK_BOOT', 'NRF51_DK_LEGACY', 'NRF51_DK_OTA', 'NRF51_DONGLE',
    'NRF51_DONGLE_BOOT', 'NRF51_DONGLE_LEGACY', 'NRF51_DONGLE_OTA', 'NRF51_MICROBIT', 'NRF51_MICROBIT_B',
    'NRF51_MICROBIT_BOOT', 'NRF51_MICROBIT_B_BOOT', 'NRF51_MICROBIT_B_OTA', 'NRF51_MICROBIT_OTA', 'NRF52_DK',
    'NUCLEO_F030R8', 'NUCLEO_F031K6', 'NUCLEO_F042K6', 'NUCLEO_F070RB', 'NUCLEO_F072RB', 'NUCLEO_F091RC',
    'NUCLEO_F103RB', 'NUCLEO_F207ZG', 'NUCLEO_F302R8', 'NUCLEO_F303K8', 'NUCLEO_F303RE', 'NUCLEO_F303ZE',
    'NUCLEO_F334R8', 'NUCLEO_F401RE', 'NUCLEO_F410RB', 'NUCLEO_F411RE', 'NUCLEO_F429ZI', 'NUCLEO_F439ZI',
    'NUCLEO_F446RE', 'NUCLEO_F446ZE', 'NUCLEO_F746ZG', 'NUCLEO_F756ZG', 'NUCLEO_F767ZI', 'NUCLEO_L011K4',
    'NUCLEO_L031K6', 'NUCLEO_L053R8', 'NUCLEO_L073RZ', 'NUCLEO_L152RE', 'NUCLEO_L432KC', 'NUCLEO_L476RG',
    'NUCLEO_L486RG', 'NUMAKER_PFM_M453', 'NUMAKER_PFM_NUC472', 'NZ32_SC151', 'OC_MBUINO', 'RBLAB_BLENANO',
    'RBLAB_BLENANO_BOOT', 'RBLAB_BLENANO_OTA', 'RBLAB_NRF51822', 'RBLAB_NRF51822_BOOT', 'RBLAB_NRF51822_OTA',
    'RZ_A1H', 'SAMD21G18A', 'SAMD21J18A', 'SAMG55J19', 'SAML21J18A', 'SAMR21G18A', 'SEEED_TINY_BLE',
    'SEEED_TINY_BLE_BOOT', 'SEEED_TINY_BLE_OTA', 'SSCI824', 'STM32F3XX', 'STM32F407', 'Super_Target', 'TEENSY3_1',
    'TY51822R3', 'TY51822R3_BOOT', 'TY51822R3_OTA', 'UBLOX_C027', 'UBLOX_EVK_ODIN_W2', 'VK_RZ_A1H', 'WALLBOT_BLE',
    'WALLBOT_BLE_BOOT', 'WALLBOT_BLE_OTA', 'WIZWIKI_W7500', 'WIZWIKI_W7500ECO', 'WIZWIKI_W7500P', 'XADOW_M0',
    'XBED_LPC1768', 'XDOT_L151CC',
]

log_file = open('log_file.log', 'w')
sys.stdout = log_file
sys.stderr = log_file

bad_targets = []
good_targets = []

for i, target in enumerate(targets):
    print('Processing target: ' + target + ' [' + str(int(100 * (float(i) / len(targets)))) + '%]')
    sys.stdout.flush()
    proc = sp.Popen('mbed compile -c -t GCC_ARM -m ' + target, stdout=sys.stdout, stderr=sys.stderr)
    proc.wait()

    if proc.returncode != 0:
        bad_targets.append(target)
    else:
        good_targets.append(target)

print('Completed. Good targets: \n')
for target in good_targets:
    print(target)
print('\nBad targets:\n')
for target in bad_targets:
    print(target)

build_path = os.path.join(os.getcwd(), 'BUILD')
targets_dirs = [os.path.join(build_path, o) for o in os.listdir(build_path) if os.path.isdir(os.path.join(build_path, o))]

targets_defs = {}

for target_dir in targets_dirs:
    target = os.path.relpath(target_dir, build_path).replace('\\', '').replace('/', '').replace(' ', '')
    config_path = os.path.join(target_dir, 'GCC_ARM', 'mbed_config.h')
    config_lines = []
    if not os.path.exists(config_path):
        continue

    with open(config_path) as f:
        config_lines += f.readlines()

    defs_start = re.compile('.*Configuration parameters.*')
    def_pattern = re.compile('^#define.+$')
    defs_started = False
    defs = []
    for line in config_lines:
        if defs_start.match(line):
            defs_started = True

        if defs_started and def_pattern.match(line):
            index = line.find('//')
            # Truncate comments
            define = re.sub("\s\s+", " ", line[: len(line) if index == -1 else index]).strip().split(' ')
            if len(define) > 3 or len(define) == 0 or len(define) == 1:
                raise Exception('Define problems')
            def_name = define[1].replace('#define', '').strip(' \t')
            if len(define) == 3:
                def_name += '=' + define[2].strip(' \t')

            targets_defs.setdefault(target, []).append(def_name)
            print(def_name)

with open('additional_defines.json', 'w') as outfile:
    json.dump(targets_defs, outfile)

print('Completed.')



