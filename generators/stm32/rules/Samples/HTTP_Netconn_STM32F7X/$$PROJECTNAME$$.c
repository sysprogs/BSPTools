#include "stm32f7xx_hal.h"
#include "lwip/netif.h"
#include "lwip/tcpip.h"
#include "lwip/dhcp.h"
#include "cmsis_os.h"
#include "ethernetif.h"

#ifdef __cplusplus
extern "C"
{
#endif

void SysTick_Handler(void);
void ETH_IRQHandler(void);
	
#ifdef __cplusplus
}
#endif

struct netif gnetif;

#pragma region IP address definition
#define IP_ADDR0   192
#define IP_ADDR1   168
#define IP_ADDR2   0
#define IP_ADDR3   10
   
#define NETMASK_ADDR0   255
#define NETMASK_ADDR1   255
#define NETMASK_ADDR2   255
#define NETMASK_ADDR3   0

#define GW_ADDR0   192
#define GW_ADDR1   168
#define GW_ADDR2   0
#define GW_ADDR3   1 
#pragma endregion

static void SystemClock_Config(void);
static void MainThread(void const * argument);
static void Netif_Config(void);
static void MPU_Config(void);

int main(void)
{
	MPU_Config();
	SCB_EnableICache();
	SCB_EnableDCache();
	HAL_Init();  
	SystemClock_Config(); 
  
	osThreadDef(Start, MainThread, osPriorityNormal, 0, configMINIMAL_STACK_SIZE * 5);
	osThreadCreate(osThread(Start), NULL);
  
	osKernelStart();
	
	/* We should never get here as control is now taken by the scheduler */
	for (;;)
		;
}

void SysTick_Handler(void)
{
	HAL_IncTick();
	osSystickHandler();
}

void ETH_IRQHandler(void)
{
	ETHERNET_IRQHandler();
}

static void MainThread(void const * argument)
{ 
	struct netconn *pListeningConnection, *pAcceptedConnection;
	err_t err;
		
	tcpip_init(NULL, NULL);
	Netif_Config();
	
	gnetif.ip_addr.addr = 0;
	gnetif.netmask.addr = 0;
	gnetif.gw.addr = 0;
	printf("Waiting for DHCP reply...\n");
	dhcp_start(&gnetif);
	while (!*((volatile u32_t *)&gnetif.ip_addr.addr))
		asm("nop");
	
	dhcp_stop(&gnetif);
	printf("Got IP address: %d.%d.%d.%d\n",
		(unsigned)gnetif.ip_addr.addr & 0xFF,
		((unsigned)gnetif.ip_addr.addr >> 8) & 0xFF,
		((unsigned)gnetif.ip_addr.addr >> 16) & 0xFF,
		((unsigned)gnetif.ip_addr.addr >> 24) & 0xFF);
  
	pListeningConnection = netconn_new(NETCONN_TCP);
	if (!pListeningConnection)
		asm("bkpt 255");
	err = netconn_bind(pListeningConnection, NULL, 80);
    
	if (err != ERR_OK)
		asm("bkpt 255");
	netconn_listen(pListeningConnection);
  
	for (;;)
	{
		err = netconn_accept(pListeningConnection, &pAcceptedConnection);
		if (err == ERR_OK)
		{
			struct netbuf *inbuf = NULL;
			err = netconn_recv(pAcceptedConnection, &inbuf);
			if (err != ERR_OK)
				asm("bkpt 255");
			netbuf_delete(inbuf);
			
			static const char HelloWorld[] = "HTTP/1.0 200 OK\r\nContent-Type: text/html\r\n\r\n<html><body><h1>Hello, World</h1>This message is shown to you by the lwIP example project.</body></html>";
			netconn_write(pAcceptedConnection,
				(const unsigned char*)HelloWorld,
				sizeof(HelloWorld),
				NETCONN_NOCOPY);
			netconn_delete(pAcceptedConnection);
		}
	}
}

static void Netif_Config(void)
{
	struct ip_addr ipaddr;
	struct ip_addr netmask;
	struct ip_addr gw;	
  
	IP4_ADDR(&ipaddr, IP_ADDR0, IP_ADDR1, IP_ADDR2, IP_ADDR3);
	IP4_ADDR(&netmask, NETMASK_ADDR0, NETMASK_ADDR1, NETMASK_ADDR2, NETMASK_ADDR3);
	IP4_ADDR(&gw, GW_ADDR0, GW_ADDR1, GW_ADDR2, GW_ADDR3);
  
	netif_add(&gnetif, &ipaddr, &netmask, &gw, NULL, &ethernetif_init, &tcpip_input);
	netif_set_default(&gnetif);
  
	if (netif_is_link_up(&gnetif))
		netif_set_up(&gnetif);
	else
		netif_set_down(&gnetif);
}

static void SystemClock_Config(void)
{
	RCC_ClkInitTypeDef RCC_ClkInitStruct;
	RCC_OscInitTypeDef RCC_OscInitStruct;
	HAL_StatusTypeDef ret = HAL_OK;

	  /* Enable HSE Oscillator and activate PLL with HSE as source */
	RCC_OscInitStruct.OscillatorType = RCC_OSCILLATORTYPE_HSE;
	RCC_OscInitStruct.HSEState = RCC_HSE_ON;
	RCC_OscInitStruct.PLL.PLLState = RCC_PLL_ON;
	RCC_OscInitStruct.PLL.PLLSource = RCC_PLLSOURCE_HSE;
	RCC_OscInitStruct.PLL.PLLM = 25;
	RCC_OscInitStruct.PLL.PLLN = 432;
	RCC_OscInitStruct.PLL.PLLP = RCC_PLLP_DIV2;
	RCC_OscInitStruct.PLL.PLLQ = 9;

	ret = HAL_RCC_OscConfig(&RCC_OscInitStruct);
	if (ret != HAL_OK)
	{
		while (1) {
			;
		}
	}

	  /* Activate the OverDrive to reach the 216 MHz Frequency */
	ret = HAL_PWREx_EnableOverDrive();
	if (ret != HAL_OK)
	{
		while (1) {
			;
		}
	}
  
	/* Select PLL as system clock source and configure the HCLK, PCLK1 and PCLK2 clocks dividers */
	RCC_ClkInitStruct.ClockType = (RCC_CLOCKTYPE_SYSCLK | RCC_CLOCKTYPE_HCLK | RCC_CLOCKTYPE_PCLK1 | RCC_CLOCKTYPE_PCLK2);
	RCC_ClkInitStruct.SYSCLKSource = RCC_SYSCLKSOURCE_PLLCLK;
	RCC_ClkInitStruct.AHBCLKDivider = RCC_SYSCLK_DIV1;
	RCC_ClkInitStruct.APB1CLKDivider = RCC_HCLK_DIV4;  
	RCC_ClkInitStruct.APB2CLKDivider = RCC_HCLK_DIV2;

	ret = HAL_RCC_ClockConfig(&RCC_ClkInitStruct, FLASH_LATENCY_7);
	if (ret != HAL_OK)
	{
		while (1) {
			;
		}
	}
}

/**
  * @brief  Configure the MPU attributes as Write Through for SRAM1/2.
  * @note   The Base Address is 0x20010000 since this memory interface is the AXI.
  *         The Region Size is 256KB, it is related to SRAM1 and SRAM2  memory size.
  * @param  None
  * @retval None
  */
static void MPU_Config(void)
{
	MPU_Region_InitTypeDef MPU_InitStruct;
  
	/* Disable the MPU */
	HAL_MPU_Disable();

	  /* Configure the MPU attributes as WT for SRAM */
	MPU_InitStruct.Enable = MPU_REGION_ENABLE;
	MPU_InitStruct.BaseAddress = 0x20010000;
	MPU_InitStruct.Size = MPU_REGION_SIZE_256KB;
	MPU_InitStruct.AccessPermission = MPU_REGION_FULL_ACCESS;
	MPU_InitStruct.IsBufferable = MPU_ACCESS_NOT_BUFFERABLE;
	MPU_InitStruct.IsCacheable = MPU_ACCESS_CACHEABLE;
	MPU_InitStruct.IsShareable = MPU_ACCESS_NOT_SHAREABLE;
	MPU_InitStruct.Number = MPU_REGION_NUMBER0;
	MPU_InitStruct.TypeExtField = MPU_TEX_LEVEL0;
	MPU_InitStruct.SubRegionDisable = 0x00;
	MPU_InitStruct.DisableExec = MPU_INSTRUCTION_ACCESS_ENABLE;

	HAL_MPU_ConfigRegion(&MPU_InitStruct);

	  /* Enable the MPU */
	HAL_MPU_Enable(MPU_PRIVILEGED_DEFAULT);
}


