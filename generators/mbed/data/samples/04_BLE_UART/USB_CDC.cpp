//This example is based on the following code: https://developer.mbed.org/teams/Bluetooth-Low-Energy/code/BLE_UARTConsole/

/* mbed Microcontroller Library
 * Copyright (c) 2006-2013 ARM Limited
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
 
#include <string.h>
#include "mbed.h"
#include "BLE.h"
 
#include "UARTService.h"
 
#define NEED_CONSOLE_OUTPUT 1 /* Set this if you need debug messages on the console;
                               * it will have an impact on code-size and power consumption. */

#ifdef DEBUG
#undef DEBUG
#endif
							   
#if NEED_CONSOLE_OUTPUT
#define DEBUG(STR) { if (uart) uart->write(STR, strlen(STR)); }
#else
#define DEBUG(...) /* nothing */
#endif /* #if NEED_CONSOLE_OUTPUT */
 
BLEDevice  g_BLE;
DigitalOut led1(LED1);
UARTService *uart;
 
void disconnectionCallback(const Gap::DisconnectionCallbackParams_t *params)
{
	DEBUG("Disconnected!\n\r");
	DEBUG("Restarting the advertising process\n\r");
	g_BLE.startAdvertising();
}
 
void periodicCallback(void)
{
	led1 = !led1;
	DEBUG("ping\r\n");
}
 
int main(void)
{
	led1 = 1;
	Ticker ticker;
	ticker.attach(periodicCallback, 1);
 
	DEBUG("Initialising the nRF51822\n\r");
	g_BLE.init();
	g_BLE.onDisconnection(disconnectionCallback);
    
	uart = new UARTService(g_BLE);
 
    /* setup advertising */
	g_BLE.accumulateAdvertisingPayload(GapAdvertisingData::BREDR_NOT_SUPPORTED);
	g_BLE.setAdvertisingType(GapAdvertisingParams::ADV_CONNECTABLE_UNDIRECTED);
	g_BLE.accumulateAdvertisingPayload(GapAdvertisingData::SHORTENED_LOCAL_NAME,
		(const uint8_t *)"BLE UART",
		sizeof("BLE UART") - 1);
	g_BLE.accumulateAdvertisingPayload(GapAdvertisingData::COMPLETE_LIST_128BIT_SERVICE_IDS,
		(const uint8_t *)UARTServiceUUID_reversed,
		sizeof(UARTServiceUUID_reversed));
 
	g_BLE.setAdvertisingInterval(160); /* 100ms; in multiples of 0.625ms. */
	g_BLE.startAdvertising();
 
	while (true) {
		g_BLE.waitForEvent();
	}
}
 