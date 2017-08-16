void __attribute__((weak)) __error__()
{
	asm("bkpt 255");
}
