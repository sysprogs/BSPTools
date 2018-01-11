#ifdef __cplusplus
extern "C"
{
#endif

#include <ets_sys.h>
#include <osapi.h>
#include <ip_addr.h>
#include <c_types.h>
#include <espconn.h>
#include <user_interface.h>
#include <upgrade.h>
#include <mem.h>

void user_init(void);
	
#ifdef __cplusplus
}
#endif

#ifdef ESP8266_GDBSTUB
#include <gdbstub.h>
#endif

void dhcps_lease_test(void)
{
	struct dhcps_lease dhcp_lease;
	IP4_ADDR(&dhcp_lease.start_ip, 192, 168, $$com.sysprogs.esp8266.http.subnet$$, 100);
	IP4_ADDR(&dhcp_lease.end_ip, 192, 168, $$com.sysprogs.esp8266.http.subnet$$, 105);
	wifi_softap_set_dhcps_lease(&dhcp_lease);
}

#ifndef EXAMPLE_BUILD_NUMBER
#define EXAMPLE_BUILD_NUMBER 1
#endif

#define QUOTE(x) #x
#define QUOTE2(x) QUOTE(x)


static char s_Message[] = "HTTP/1.1 200 OK\r\nContent-type: text/html\r\n\r\n<html><body><h1>Hello, World</h1>This page is served by HTTP Server build " QUOTE2(EXAMPLE_BUILD_NUMBER) ". <a href=\"http://192.168.$$com.sysprogs.esp8266.http.subnet$$.1:88/\">Begin upgrade</a>.</body></html>";
static char s_UpgradeMessage[] = "HTTP/1.1 200 OK\r\nContent-type: text/html\r\n\r\n<html><body><h1>OTA upgrade initiated</h1>If your computer is running ESPImageTool on port 8888, the upgrade will happen automatically.  <a href=\"http://192.168.$$com.sysprogs.esp8266.http.subnet$$.1/\">Go back to main page</a>.</body></html>";

static void ota_finished_callback(void *arg)
{
    upgrade_server_info *pUpdate = (upgrade_server_info *)arg;
    if (pUpdate->upgrade_flag == true)
    {
        system_upgrade_reboot();
    }
    else
    {
    }
}

static void __attribute__((section(".irom.text"))) upgradeCb(void *arg) 
{
	struct espconn *pConn = (struct espconn*)arg;
    espconn_sent(pConn, (uint8_t *)s_UpgradeMessage, strlen(s_UpgradeMessage));
	espconn_disconnect(pConn);
    
    upgrade_server_info *pUpgrade = (upgrade_server_info *)pvPortZalloc(sizeof(upgrade_server_info), "", 0);
	pUpgrade->pespconn = pConn;
    memcpy(pUpgrade->ip, pConn->proto.tcp->remote_ip, 4);
    pUpgrade->check_cb = ota_finished_callback;
    pUpgrade->check_times = 60000;
    pUpgrade->port = 8888;
    pUpgrade->upgrade_version[0] = '1';
    if (system_upgrade_userbin_check() == UPGRADE_FW_BIN1)
        pUpgrade->url = (uint8_t *)"GET /user2.bin HTTP/1.0\r\nConnection: close\r\n\r\n\r";
    else
        pUpgrade->url = (uint8_t *)"GET /user1.bin HTTP/1.0\r\nConnection: close\r\n\r\n\r";
    
    system_upgrade_start(pUpgrade);
}

static void __attribute__((section(".irom.text"))) httpdConnectCb(void *arg) 
{
    struct espconn *pConn = (struct espconn*)arg;
    espconn_sent(pConn, (uint8_t *)s_Message, strlen(s_Message));
    espconn_disconnect(pConn);
}
/*
	How to use this example:
		1. Build & program it to your ESP8266
		2. Connect to the $$com.sysprogs.esp8266.http.ssid$$ WiFi network from your computer
		3. Open http://192.168.$$com.sysprogs.esp8266.http.subnet$$.1/ in your browser
*/

void user_init()
{
#ifdef ESP8266_GDBSTUB
	gdbstub_init();
#endif

	//Uncomment the line below if you want to step through the initialization function in the debugger without getting a reset from a watchdog.
	//system_soft_wdt_stop();
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
	
    static esp_tcp httpdTcp;
    httpdTcp.local_port = 80;
    static struct espconn httpdConn = { .type = ESPCONN_TCP, .state = ESPCONN_NONE };
	httpdConn.proto.tcp = &httpdTcp;
    
    static esp_tcp upgradeTcp;
    upgradeTcp.local_port = 88;
    static struct espconn upgradeConn = { .type = ESPCONN_TCP, .state = ESPCONN_NONE };
    upgradeConn.proto.tcp = &upgradeTcp;

	espconn_regist_connectcb(&httpdConn, httpdConnectCb);
	espconn_accept(&httpdConn);
    
    espconn_regist_connectcb(&upgradeConn, upgradeCb);
    espconn_accept(&upgradeConn);
}
