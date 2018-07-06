extern "C"
{
	int __attribute__((weak)) _kill()
	{
		return 0;
	}
	
	int __attribute__((weak)) _getpid()
	{
		return 0;
	}

	int __attribute__((weak)) initialise_monitor_handles()
	{
		return 0;
	}
	
	//Only needed for nRF5x-based targets. Will get overridden once the BLE-related frameworks are added.
	void __attribute__((weak)) assert_nrf_callback(unsigned short line_num, const unsigned char *p_file_name)
	{
		asm("bkpt 255");
	}
}