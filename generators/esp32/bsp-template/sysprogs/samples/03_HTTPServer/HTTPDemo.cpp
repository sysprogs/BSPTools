/* HTTP GET Example using plain POSIX sockets

   This example code is in the Public Domain (or CC0 licensed, at your option.)

   Unless required by applicable law or agreed to in writing, this
   software is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
   CONDITIONS OF ANY KIND, either express or implied.
*/
#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/event_groups.h"
#include "esp_system.h"
#include "esp_wifi.h"
#include "esp_event_loop.h"
#include "esp_log.h"
#include "nvs_flash.h"

#include "lwip/err.h"
#include "lwip/sockets.h"
#include "lwip/sys.h"
#include "lwip/netdb.h"
#include "lwip/dns.h"


static esp_err_t event_handler(void *ctx, system_event_t *event)
{
    return ESP_OK;
}

static void initialize_wifi(void)
{
    tcpip_adapter_init();
    tcpip_adapter_ip_info_t info = { 0, };
    IP4_ADDR(&info.ip, 192, 168, $$com.sysprogs.esp32.http.subnet$$, 1);
    IP4_ADDR(&info.gw, 192, 168, $$com.sysprogs.esp32.http.subnet$$, 1);
    IP4_ADDR(&info.netmask, 255, 255, 255, 0);
    ESP_ERROR_CHECK(tcpip_adapter_dhcps_stop(TCPIP_ADAPTER_IF_AP));
    ESP_ERROR_CHECK(tcpip_adapter_set_ip_info(TCPIP_ADAPTER_IF_AP, &info));
    ESP_ERROR_CHECK(tcpip_adapter_dhcps_start(TCPIP_ADAPTER_IF_AP));
    ESP_ERROR_CHECK(esp_event_loop_init(event_handler, NULL));
    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&cfg));
    ESP_ERROR_CHECK(esp_wifi_set_storage(WIFI_STORAGE_RAM));
    
    wifi_config_t wifi_config;
    memset(&wifi_config, 0, sizeof(wifi_config));
    strcpy(wifi_config.ap.ssid, "$$com.sysprogs.esp32.http.ssid$$");
    wifi_config.ap.ssid_len = strlen(wifi_config.ap.ssid);
    wifi_config.ap.max_connection = 4;
    
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_AP));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_AP, &wifi_config));
    ESP_ERROR_CHECK(esp_wifi_start());
    asm("nop");
}


static const char szHeader[] = "HTTP/1.0 200 OK\r\nContent-type: text/html\r\n\r\n<html><body><h1>Hello, world</h1>You have requested the following URL: ";
static const char szFooter[] = "</body></html>";

void ServerTask(void *pvParameters)
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

extern "C" void app_main()
{
    nvs_flash_init();
    initialize_wifi();
    xTaskCreate(&ServerTask, "ServerTask", 2048, NULL, 5, NULL);
}
