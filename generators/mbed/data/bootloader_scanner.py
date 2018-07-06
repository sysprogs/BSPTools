#Based on MCU_NRF51Code.binary_hook()
def LocateNordicSoftdeviceAndBootloader(t_self, resources):
    result = []
    for softdevice_and_offset_entry in t_self.target.EXPECTED_SOFTDEVICES_WITH_OFFSETS:
        for hexf in resources.hex_files:
            if hexf.find(softdevice_and_offset_entry['name']) != -1:
                result.append(hexf)

            if len(result) == 0:
                continue

            if t_self.target.MERGE_BOOTLOADER is True:
                for hexf in resources.hex_files:
                    if hexf.find(t_self.target.OVERRIDE_BOOTLOADER_FILENAME) != -1:
                        result.append(t_self.target.OVERRIDE_BOOTLOADER_FILENAME)
                        break;
                    elif hexf.find(softdevice_and_offset_entry['boot']) != -1:
                        result.append(hexf)
                        break;
            return result


def LocateHexFiles(toolchain, resources):
    try:
        hook = toolchain.target.post_binary_hook['function']
    except:
        hook = None
    if hook == "MCU_NRF51Code.binary_hook":
        return LocateNordicSoftdeviceAndBootloader(toolchain, resources)
    elif "NORDIC" in toolchain.target.extra_labels:
        if len(resources.hex_files) != 1:
            raise Exception("Unexpected hex file count for " + t_self.target.name)
        return resources.hex_files
    return None
