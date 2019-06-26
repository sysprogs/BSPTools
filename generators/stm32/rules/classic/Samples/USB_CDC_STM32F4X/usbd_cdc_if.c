/**
  ******************************************************************************
  * @file    usbd_cdc_if_template.c
  * @author  MCD Application Team
  * @version V2.4.0
  * @date    28-February-2015
  * @brief   Generic media access Layer.
  ******************************************************************************
  * @attention
  *
  * <h2><center>&copy; COPYRIGHT 2015 STMicroelectronics</center></h2>
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
#include "usbd_cdc_if.h"

/** @addtogroup STM32_USB_DEVICE_LIBRARY
  * @{
  */


/** @defgroup USBD_CDC 
  * @brief usbd core module
  * @{
  */ 

/** @defgroup USBD_CDC_Private_TypesDefinitions
  * @{
  */ 
/**
  * @}
  */ 


/** @defgroup USBD_CDC_Private_Defines
  * @{
  */ 
/**
  * @}
  */ 


/** @defgroup USBD_CDC_Private_Macros
  * @{
  */ 

/**
  * @}
  */ 


/** @defgroup USBD_CDC_Private_FunctionPrototypes
  * @{
  */

static int8_t $$PROJECTNAME$$_Init     (void);
static int8_t $$PROJECTNAME$$_DeInit   (void);
static int8_t $$PROJECTNAME$$_Control  (uint8_t cmd, uint8_t* pbuf, uint16_t length);
static int8_t $$PROJECTNAME$$_Receive  (uint8_t* pbuf, uint32_t *Len);

USBD_CDC_ItfTypeDef USBD_CDC_$$PROJECTNAME$$_fops = 
{
  $$PROJECTNAME$$_Init,
  $$PROJECTNAME$$_DeInit,
  $$PROJECTNAME$$_Control,
  $$PROJECTNAME$$_Receive
};

USBD_CDC_LineCodingTypeDef linecoding =
  {
    115200, /* baud rate*/
    0x00,   /* stop bits-1*/
    0x00,   /* parity - none*/
    0x08    /* nb. of bits 8*/
  };

/* Private functions ---------------------------------------------------------*/

/**
  * @brief  $$PROJECTNAME$$_Init
  *         Initializes the CDC media low layer
  * @param  None
  * @retval Result of the operation: USBD_OK if all operations are OK else USBD_FAIL
  */
extern USBD_HandleTypeDef USBD_Device;

static struct
{
	uint8_t Buffer[CDC_DATA_HS_OUT_PACKET_SIZE];
	int Position, Size;
	char ReadDone;
} s_RxBuffer;

char g_VCPInitialized;

static int8_t $$PROJECTNAME$$_Init(void)
{
	USBD_CDC_SetRxBuffer(&USBD_Device, s_RxBuffer.Buffer);
	g_VCPInitialized = 1;
	return (0);
}

/**
  * @brief  $$PROJECTNAME$$_DeInit
  *         DeInitializes the CDC media low layer
  * @param  None
  * @retval Result of the operation: USBD_OK if all operations are OK else USBD_FAIL
  */
static int8_t $$PROJECTNAME$$_DeInit(void)
{
  /*
     Add your deinitialization code here 
  */  
  return (0);
}


/**
  * @brief  $$PROJECTNAME$$_Control
  *         Manage the CDC class requests
  * @param  Cmd: Command code            
  * @param  Buf: Buffer containing command data (request parameters)
  * @param  Len: Number of data to be sent (in bytes)
  * @retval Result of the operation: USBD_OK if all operations are OK else USBD_FAIL
  */
static int8_t $$PROJECTNAME$$_Control  (uint8_t cmd, uint8_t* pbuf, uint16_t length)
{ 
  switch (cmd)
  {
  case CDC_SEND_ENCAPSULATED_COMMAND:
    /* Add your code here */
    break;

  case CDC_GET_ENCAPSULATED_RESPONSE:
    /* Add your code here */
    break;

  case CDC_SET_COMM_FEATURE:
    /* Add your code here */
    break;

  case CDC_GET_COMM_FEATURE:
    /* Add your code here */
    break;

  case CDC_CLEAR_COMM_FEATURE:
    /* Add your code here */
    break;

  case CDC_SET_LINE_CODING:
    linecoding.bitrate    = (uint32_t)(pbuf[0] | (pbuf[1] << 8) |\
                            (pbuf[2] << 16) | (pbuf[3] << 24));
    linecoding.format     = pbuf[4];
    linecoding.paritytype = pbuf[5];
    linecoding.datatype   = pbuf[6];
    
    /* Add your code here */
    break;

  case CDC_GET_LINE_CODING:
    pbuf[0] = (uint8_t)(linecoding.bitrate);
    pbuf[1] = (uint8_t)(linecoding.bitrate >> 8);
    pbuf[2] = (uint8_t)(linecoding.bitrate >> 16);
    pbuf[3] = (uint8_t)(linecoding.bitrate >> 24);
    pbuf[4] = linecoding.format;
    pbuf[5] = linecoding.paritytype;
    pbuf[6] = linecoding.datatype;     
    
    /* Add your code here */
    break;

  case CDC_SET_CONTROL_LINE_STATE:
    /* Add your code here */
    break;

  case CDC_SEND_BREAK:
     /* Add your code here */
    break;    
    
  default:
    break;
  }

  return (0);
}

/**
  * @brief  $$PROJECTNAME$$_Receive
  *         Data received over USB OUT endpoint are sent over CDC interface 
  *         through this function.
  *           
  *         @note
  *         This function will issue a NAK packet on any OUT packet received on 
  *         USB endpoint untill exiting this function. If you exit this function
  *         before transfer is complete on CDC interface (ie. using DMA controller)
  *         it will result in receiving more data while previous ones are still 
  *         not sent.
  *                 
  * @param  Buf: Buffer of data to be received
  * @param  Len: Number of data received (in bytes)
  * @retval Result of the operation: USBD_OK if all operations are OK else USBD_FAIL
  */
static int8_t $$PROJECTNAME$$_Receive(uint8_t* Buf, uint32_t *Len)
{
	s_RxBuffer.Position = 0;
	s_RxBuffer.Size = *Len;
	s_RxBuffer.ReadDone = 1;
	return (0);
}

/**
  * @}
  */ 

/**
  * @}
  */ 

/**
  * @}
  */ 

/************************ (C) COPYRIGHT STMicroelectronics *****END OF FILE****/

int VCP_read(void *pBuffer, int size)
{
	if (!s_RxBuffer.ReadDone)
		return 0;

	int remaining = s_RxBuffer.Size - s_RxBuffer.Position;
	int todo = MIN(remaining, size);
	if (todo <= 0)
		return 0;

	memcpy(pBuffer, s_RxBuffer.Buffer + s_RxBuffer.Position, todo);
	s_RxBuffer.Position += todo;
	if (s_RxBuffer.Position >= s_RxBuffer.Size)
	{
		s_RxBuffer.ReadDone = 0;
		USBD_CDC_ReceivePacket(&USBD_Device);
	}

	return todo;
}

#ifdef USE_USB_HS
enum { kMaxOutPacketSize = CDC_DATA_HS_OUT_PACKET_SIZE };
#else
enum { kMaxOutPacketSize = CDC_DATA_FS_OUT_PACKET_SIZE };
#endif

int VCP_write(const void *pBuffer, int size)
{
    if (size > kMaxOutPacketSize)
	{
		int offset;
    	int done = 0;
    	for (offset = 0; offset < size; offset += done)
		{
    		int todo = MIN(kMaxOutPacketSize, size - offset);
			done = VCP_write(((char *)pBuffer) + offset, todo);
			if (done != todo)
				return offset + done;
		}

		return size;
	}

	USBD_CDC_HandleTypeDef *pCDC =
	        (USBD_CDC_HandleTypeDef *)USBD_Device.pClassData;
	while (pCDC->TxState) {} //Wait for previous transfer

	USBD_CDC_SetTxBuffer(&USBD_Device, (uint8_t *)pBuffer, size);
	if (USBD_CDC_TransmitPacket(&USBD_Device) != USBD_OK)
		return 0;

	while (pCDC->TxState) {} //Wait until transfer is done
	return size;
}
