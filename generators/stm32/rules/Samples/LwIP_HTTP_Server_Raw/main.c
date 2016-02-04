/**
  ******************************************************************************
  * @file    LwIP/LwIP_HTTP_Server_Raw/Src/main.c 
  * @author  MCD Application Team
  * @version V1.1.1
  * @date    20-November-2015
  * @brief   This sample code implements a http server application based on Raw
  *          API of LwIP stack. This application uses STM32F2xx the ETH HAL API 
  *          to transmit and receive data. 
  *          The communication is done with a web browser of a remote PC.
  ******************************************************************************
  * @attention
  *
  * <h2><center>&copy; COPYRIGHT(c) 2015 STMicroelectronics</center></h2>
  *
  * Licensed under MCD-ST Liberty SW License Agreement V2, (the "License");
  * You may not use this file except in compliance with the License.
  * You may obtain a copy of the License at:
  *
  *        http://www.st.com/software_license_agreement_liberty_v2
  *
  * Unless required by applicable law or agreed to in writing, software 
  * distributed under the License is distributed on an "AS IS" BASIS, 
  * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  * See the License for the specific language governing permissions and
  * limitations under the License.
  *
  ******************************************************************************
  */

/* Includes ------------------------------------------------------------------*/
#include "lwip/opt.h"
#include "lwip/init.h"
#include "netif/etharp.h"
#include "lwip/netif.h"
#include "lwip/lwip_timers.h"
#include "ethernetif.h"
#include "main.h"
#include "app_ethernet.h"

#include "httpd.h"
#include "lwip/tcp.h"

/* Private typedef -----------------------------------------------------------*/
/* Private define ------------------------------------------------------------*/
/* Private macro -------------------------------------------------------------*/
/* Private variables ---------------------------------------------------------*/
struct netif gnetif;

/* Private function prototypes -----------------------------------------------*/
static void BSP_Config(void);
static void Netif_Config(void);
static void SystemClock_Config(void);
static void Error_Handler(void);

/* Private functions ---------------------------------------------------------*/

/**
  * @brief  Main program
  * @param  None
  * @retval None
  */
int main(void)
{
  /* STM32F2xx HAL library initialization:
       - Configure the Flash prefetch, instruction and Data caches
       - Configure the Systick to generate an interrupt each 1 msec
       - Set NVIC Group Priority to 4
       - Global MSP (MCU Support Package) initialization
     */
  HAL_Init();  
  
  /* Configure the system clock to 120 MHz */
  SystemClock_Config();
  
  /* Configure the BSP */
  BSP_Config();
  
  /* Initialize the LwIP stack */
  lwip_init();
  
  /* Configure the Network interface */
  Netif_Config();
  
  /* Http webserver Init */
  httpd_init();
  
  /* Notify user about the network interface config */
//  User_notification(&gnetif);

  /* Infinite loop */
  while (1)
  { 
    /* Read a received packet from the Ethernet buffers and send it 
       to the lwIP for handling */
    ethernetif_input(&gnetif);

    /* Handle timeouts */
    sys_check_timeouts();

#ifdef USE_DHCP
    /* handle periodic timers for LwIP */
    DHCP_Periodic_Handle(&gnetif);
#endif 
  } 
}

/**
* @brief  Initializes the lwIP stack
* @param  None
* @retval None
*/
static void Netif_Config(void)
{
  struct ip_addr ipaddr;
  struct ip_addr netmask;
  struct ip_addr gw;
  
  /* IP address default setting */
  IP4_ADDR(&ipaddr, IP_ADDR0, IP_ADDR1, IP_ADDR2, IP_ADDR3);
  IP4_ADDR(&netmask, NETMASK_ADDR0, NETMASK_ADDR1 , NETMASK_ADDR2, NETMASK_ADDR3);
  IP4_ADDR(&gw, GW_ADDR0, GW_ADDR1, GW_ADDR2, GW_ADDR3); 
  
  /* add the network interface */    
  netif_add(&gnetif, &ipaddr, &netmask, &gw, NULL, &ethernetif_init, &ethernet_input);
  
  /*  Registers the default network interface */
  netif_set_default(&gnetif);
  
  if (netif_is_link_up(&gnetif))
  {
    /* When the netif is fully configured this function must be called */
    netif_set_up(&gnetif);
  }
  else
  {
    /* When the netif link is down this function must be called */
    netif_set_down(&gnetif);
  }
  
  /* Set the link callback function, this function is called on change of link status*/
  netif_set_link_callback(&gnetif, ethernetif_update_config);
}

/**
  * @brief  Initializes the STM322xG-EVAL's LCD and LEDs resources.
  * @param  None
  * @retval None
  */
static void BSP_Config(void)
{  
 
}

/**
  * @brief  EXTI line detection callbacks
  * @param  GPIO_Pin: Specifies the pins connected EXTI line
  * @retval None
  */
void HAL_GPIO_EXTI_Callback(uint16_t GPIO_Pin)
{
  if (GPIO_Pin == GPIO_PIN_14)
  {
    ethernetif_set_link(&gnetif);
  }
}

/**
  * @brief  System Clock Configuration
 
  * @param  None
  * @retval None
  */
static void SystemClock_Config(void)
{

}

/**
  * @brief  This function is executed in case of error occurrence.
  * @param  None
  * @retval None
  */
static void Error_Handler(void)
{
  /* User may add here some code to deal with this error */
  while(1)
  {
  }
}

#ifdef  USE_FULL_ASSERT
/**
  * @brief  Reports the name of the source file and the source line number
  *         where the assert_param error has occurred.
  * @param  file: pointer to the source file name
  * @param  line: assert_param error line source number
  * @retval None
  */
void assert_failed(uint8_t* file, uint32_t line)
{
  /* User can add his own implementation to report the file name and line number,
     ex: printf("Wrong parameters value: file %s on line %d\r\n", file, line) */

  /* Infinite loop */
  while (1)
  {
  }
}
#endif

//------------ httpd -------------------------
//------------ httpd -------------------------

/** The server port for HTTPD to use */
#ifndef HTTPD_SERVER_PORT
#define HTTPD_SERVER_PORT                   80
#endif

/** Priority for tcp pcbs created by HTTPD (very low by default).
 *  Lower priorities get killed first when running out of memroy.
 */
#ifndef HTTPD_TCP_PRIO
#define HTTPD_TCP_PRIO                      TCP_PRIO_MIN
#endif

/** Set this to 1 to enabled timing each file sent */
#ifndef LWIP_HTTPD_TIMING
#define LWIP_HTTPD_TIMING                   0
#endif
#ifndef HTTPD_DEBUG_TIMING
#define HTTPD_DEBUG_TIMING                  LWIP_DBG_OFF
#endif
#ifndef HTTPD_DEBUG
#define HTTPD_DEBUG         LWIP_DBG_OFF
#endif
/**
 * A new incoming connection has been accepted.
 */
static err_t http_accept(void *arg, struct tcp_pcb *pcb, err_t err)
{
	return ERR_OK;
}
/**
 * Initialize the httpd with the specified local address.
 */
static void httpd_init_addr(struct ip_addr *local_addr)
{
  struct tcp_pcb *pcb;
  err_t err;

  pcb = tcp_new();
  LWIP_ASSERT("httpd_init: tcp_new failed", pcb != NULL);
  tcp_setprio(pcb, HTTPD_TCP_PRIO);
  /* set SOF_REUSEADDR here to explicitly bind httpd to multiple interfaces */
  err = tcp_bind(pcb, local_addr, HTTPD_SERVER_PORT);
  LWIP_ASSERT("httpd_init: tcp_bind failed", err == ERR_OK);
  pcb = tcp_listen(pcb);
  LWIP_ASSERT("httpd_init: tcp_listen failed", pcb != NULL);
  /* initialize callback arg and accept callback */
  tcp_arg(pcb, pcb);
  tcp_accept(pcb, http_accept);
}
/**
 * Initialize the httpd: set up a listening PCB and bind it to the defined port
 */
void httpd_init(void)
{
  LWIP_DEBUGF(HTTPD_DEBUG, ("httpd_init\n"));

#if LWIP_HTTPD_SSI
  httpd_ssi_init();
#endif
  
#if LWIP_HTTPD_CGI
  httpd_cgi_init();
#endif
  
  httpd_init_addr(IP_ADDR_ANY);
}


/************************ (C) COPYRIGHT STMicroelectronics *****END OF FILE****/
