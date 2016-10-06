int main();

extern int _bss_start, _bss_end;

void flashless_entry()
{
	int *p = &_bss_start;
	while (p < &_bss_end)
		*p++ = 0;
		
	main();
}