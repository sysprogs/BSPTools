#include <mbed.h>
#include <USBSerial.h>
 
USBSerial g_USBSerial;

#ifdef DEBUG
/*
	Defining DEBUG will enable a LOT of debug output in the USBDevice.cpp file inside mbed.
	This may prevent the device from answering to USB requests in time and will make it unrecognizable.
	Please replace #ifdef DEBUG with #ifdef DEBUG_USBDEVICE in USBDevice.cpp and remove the error directive below.
*/
#error Please fix USBDevice.cpp as described above 
#endif

int main()
{
	for (;;)
	{
		while (!g_USBSerial.readable())
			;
		char ch = g_USBSerial.getc();
		while (!g_USBSerial.writeable())
			;
		g_USBSerial.putc(ch + 1);
	}
}
 