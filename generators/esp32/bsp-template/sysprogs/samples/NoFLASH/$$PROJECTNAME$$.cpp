int g_Counter;

int main()
{
	for (;;)
	{
		asm("nop");
		asm("nop");
		g_Counter++;
		asm("nop");
		asm("nop");
	}
}

#ifdef __cplusplus
extern "C" 
#endif
int flashless_entry()
{
	main();    
    return 0;
}