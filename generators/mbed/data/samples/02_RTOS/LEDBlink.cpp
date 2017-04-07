#include <mbed.h>
#include <rtos.h>
 
DigitalOut g_LED1(LED$$com.sysprogs.examples.ledblink.LED1NUM$$);
DigitalOut g_LED2(LED$$com.sysprogs.examples.ledblink.LED2NUM$$);

static void ThreadBody(const void *) 
{
	for (;;)
	{
		g_LED1 = !g_LED1;
		Thread::wait($$com.sysprogs.examples.ledblink.DELAY1MSEC$$);
	}
}

int main()
{
	Thread thread(ThreadBody);
	for (;;)
	{
		g_LED2 = !g_LED2;
		Thread::wait($$com.sysprogs.examples.ledblink.DELAY2MSEC$$);
	}
}
