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
}