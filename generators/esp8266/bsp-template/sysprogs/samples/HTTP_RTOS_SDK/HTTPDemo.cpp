#ifdef __cplusplus
extern "C"
{
#endif
#include "esp_common.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include "lwip/sockets.h"
#include "lwip/dns.h"
#include "lwip/netdb.h"

void user_init(void);

#ifdef __cplusplus
}
#endif

#ifdef ESP8266_GDBSTUB
#include <gdbstub.h>
#endif

#define RAMFUNC __attribute__((section(".entry.text")))

static const char szHeader[] = "HTTP/1.0 200 OK\r\nContent-type: text/html\r\n\r\n<html><body><h1>Hello, world</h1>You have requested the following URL: ";
static const char szFooter[] = "</body></html>";

void RAMFUNC ServerTask(void *pvParameters)
{
	struct sockaddr_in server_addr, client_addr;
	int server_sock;
	socklen_t sin_size = sizeof(client_addr);
	bzero(&server_addr, sizeof(struct sockaddr_in));
	server_addr.sin_family = AF_INET;
	server_addr.sin_addr.s_addr = INADDR_ANY;
	server_addr.sin_port = htons(80);

	server_sock = socket(AF_INET, SOCK_STREAM, 0);
	if (server_sock == -1)
	{
		asm("break 1,1");
	}

	if (bind(server_sock, (struct sockaddr *)(&server_addr), sizeof(struct sockaddr)))
	{
		asm("break 1,1");
	}

	if (listen(server_sock, 5)) 
	{
		asm("break 1,1");
	}

	for (;;) 
	{
		int client_sock = accept(server_sock, (struct sockaddr *) &client_addr, &sin_size);
		static char szBuf[4096];
		int readPos = 0;
		char *pURL;
		
		if (client_sock < 0) 
		{
			asm("break 1,1");
			continue;
		}
		
		//Read the entire HTTP request
		for (;;)
		{
			int done = read(client_sock, szBuf + readPos, sizeof(szBuf) - readPos - 1);
			if (done < 0 || done >= (sizeof(szBuf) - readPos))
				done = 0;
			
			readPos += done;
			szBuf[readPos] = 0;
			if (strstr(szBuf, "\r\n\r\n"))
				break;
			if (!done)
				break;
		}
		
		pURL = strchr(szBuf, ' ');
		if (pURL)
		{
			char *pURLEnd = strchr(pURL + 1, ' ');
			if (pURLEnd)
			{
				pURL++;
				pURLEnd[0] = 0;
				write(client_sock, szHeader, sizeof(szHeader) - 1);
				write(client_sock, pURL, strlen(pURL));
				write(client_sock, szFooter, sizeof(szFooter) - 1);
			}
		}

		close(client_sock);
	}
}

void dhcps_lease_test(void)
{
	struct dhcps_lease dhcp_lease;
	IP4_ADDR(&dhcp_lease.start_ip, 192, 168, $$com.sysprogs.esp8266.http.subnet$$, 100);
	IP4_ADDR(&dhcp_lease.end_ip, 192, 168, $$com.sysprogs.esp8266.http.subnet$$, 105);
	wifi_softap_set_dhcps_lease(&dhcp_lease);
}

/*
	How to use this example:
		1. Build & program it to your ESP8266
		2. Connect to the $$com.sysprogs.esp8266.http.ssid$$ WiFi network from your computer
		3. Open http://192.168.$$com.sysprogs.esp8266.http.subnet$$.1/test in your browser
*/

void RAMFUNC user_init(void)
{
#ifdef ESP8266_GDBSTUB
	gdbstub_init();
#endif

	struct ip_info info;
	struct softap_config cfg;
	wifi_softap_get_config(&cfg);
	strcpy((char *)cfg.ssid, "$$com.sysprogs.esp8266.http.ssid$$");
	cfg.ssid_len = strlen((char*)cfg.ssid);
	wifi_softap_set_config_current(&cfg);
	wifi_set_opmode(SOFTAP_MODE);
	
	wifi_softap_dhcps_stop();
	IP4_ADDR(&info.ip, 192, 168, $$com.sysprogs.esp8266.http.subnet$$, 1);
	IP4_ADDR(&info.gw, 192, 168, $$com.sysprogs.esp8266.http.subnet$$, 1);
	IP4_ADDR(&info.netmask, 255, 255, 255, 0);
	wifi_set_ip_info(SOFTAP_IF, &info);
	dhcps_lease_test();
	wifi_softap_dhcps_start();

	xTaskCreate(ServerTask, (signed char *)"Server", 256, NULL, 2, NULL);
}

