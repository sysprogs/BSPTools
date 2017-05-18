#include "spi_flash.h"
#include <dport_reg.h>
#include <rtc_cntl_reg.h>
#include <timer_group_struct.h>
#include <timer_group_reg.h>
#include <uart_reg.h>

enum FLASHCommandType
{
    Initialize,
    Erase,
    Program,
    Reset,
};

struct Header
{
    int Signature;
    void *LoadAddress;
    void *EntryPoint;
    void *InfiniteLoop;
    void *ChipReset;
    void *DataBuffer;
    unsigned DataBufferSize;
    void *Stack;
    unsigned StackSize;
};

static int s_SectorSize;

extern "C" void FLASHHelperEntry(int command, int Arg1, int Arg2);
void ResetChip();

char s_DataBuffer[65536];
char s_Stack[1024];

__attribute__((section(".text"))) unsigned char InfiniteLoop[] = { 0x06, 0xff, 0xff };
//__attribute__((section(".text"))) unsigned char BreakDemo[] = { 0x3d  ,  0xf0  ,  0x10  ,  0x41   , 0x00   , 0x7c};

Header s_Header __attribute__((section(".headers"))) = { 
    '23+F',
    &s_Header,
    (void *)&FLASHHelperEntry,
    (void *)&InfiniteLoop,
    (void *)&ResetChip,
    s_DataBuffer,
    sizeof(s_DataBuffer),
    s_Stack,
    sizeof(s_Stack)
};

extern "C" 
{
    void Cache_Read_Disable(int);
    void Cache_Flush(int);
    void mmu_init(int);
    void rtc_set_cpu_freq(int);
    void xt_ints_off(int);
}

static inline uint32_t __attribute__((always_inline)) xPortGetCoreID() {
    int id;
    asm volatile(
        "rsr.prid %0\n"
        " extui %0,%0,13,1"
        : "=r"(id));
    return id;
}

static void esp_cpu_stall(int cpu_id)
{
    if (cpu_id == 1) {
        CLEAR_PERI_REG_MASK(RTC_CNTL_SW_CPU_STALL_REG, RTC_CNTL_SW_STALL_APPCPU_C1_M);
        SET_PERI_REG_MASK(RTC_CNTL_SW_CPU_STALL_REG, 0x21 << RTC_CNTL_SW_STALL_APPCPU_C1_S);
        CLEAR_PERI_REG_MASK(RTC_CNTL_OPTIONS0_REG, RTC_CNTL_SW_STALL_APPCPU_C0_M);
        SET_PERI_REG_MASK(RTC_CNTL_OPTIONS0_REG, 2 << RTC_CNTL_SW_STALL_APPCPU_C0_S);
    }
    else {
        CLEAR_PERI_REG_MASK(RTC_CNTL_SW_CPU_STALL_REG, RTC_CNTL_SW_STALL_PROCPU_C1_M);
        SET_PERI_REG_MASK(RTC_CNTL_SW_CPU_STALL_REG, 0x21 << RTC_CNTL_SW_STALL_PROCPU_C1_S);
        CLEAR_PERI_REG_MASK(RTC_CNTL_OPTIONS0_REG, RTC_CNTL_SW_STALL_PROCPU_C0_M);
        SET_PERI_REG_MASK(RTC_CNTL_OPTIONS0_REG, 2 << RTC_CNTL_SW_STALL_PROCPU_C0_S);
    }
}

static inline void uart_tx_wait_idle(uint8_t uart_no) {
    while (REG_GET_FIELD(UART_STATUS_REG(uart_no), UART_ST_UTX_OUT)) {
        ;
    }
}


static void ResetPeripherals()
{
    const uint32_t core_id = xPortGetCoreID();
    const uint32_t other_core_id = core_id == 0 ? 1 : 0;
    esp_cpu_stall(other_core_id);

    // We need to disable TG0/TG1 watchdogs
    // First enable RTC watchdog to be on the safe side
    REG_WRITE(RTC_CNTL_WDTWPROTECT_REG, RTC_CNTL_WDT_WKEY_VALUE);
    REG_WRITE(RTC_CNTL_WDTCONFIG0_REG,
        RTC_CNTL_WDT_FLASHBOOT_MOD_EN_M |
        (1 << RTC_CNTL_WDT_SYS_RESET_LENGTH_S) |
        (1 << RTC_CNTL_WDT_CPU_RESET_LENGTH_S));
    REG_WRITE(RTC_CNTL_WDTCONFIG1_REG, 128000);

    // Disable TG0/TG1 watchdogs
    TIMERG0.wdt_wprotect = TIMG_WDT_WKEY_VALUE;
    TIMERG0.wdt_config0.en = 0;
    TIMERG0.wdt_wprotect = 0;
    TIMERG1.wdt_wprotect = TIMG_WDT_WKEY_VALUE;
    TIMERG1.wdt_config0.en = 0;
    TIMERG1.wdt_wprotect = 0;

    // Disable all interrupts
    xt_ints_off(0xFFFFFFFF);

    // Disable cache
    Cache_Read_Disable(0);
    Cache_Read_Disable(1);

    // Flush any data left in UART FIFOs
    uart_tx_wait_idle(0);
    uart_tx_wait_idle(1);
    uart_tx_wait_idle(2);

    // Reset wifi/bluetooth/ethernet/sdio (bb/mac)
    SET_PERI_REG_MASK(DPORT_CORE_RST_EN_REG, 
        DPORT_BB_RST | DPORT_FE_RST | DPORT_MAC_RST |
        DPORT_BT_RST | DPORT_BTMAC_RST | DPORT_SDIO_RST |
        DPORT_SDIO_HOST_RST | DPORT_EMAC_RST | DPORT_MACPWR_RST | 
        DPORT_RW_BTMAC_RST | DPORT_RW_BTLP_RST);
    REG_WRITE(DPORT_CORE_RST_EN_REG, 0);

    // Reset timer/spi/uart
    SET_PERI_REG_MASK(DPORT_PERIP_RST_EN_REG,
        DPORT_TIMERS_RST | DPORT_SPI_RST_1 | DPORT_UART_RST);
    REG_WRITE(DPORT_PERIP_RST_EN_REG, 0);

    // Set CPU back to XTAL source, no PLL, same as hard reset
    rtc_set_cpu_freq(0 /*CPU_XTAL*/);
}

static inline void esp_cpu_unstall(int cpu_id)
{
    if (cpu_id == 1) {
        CLEAR_PERI_REG_MASK(RTC_CNTL_SW_CPU_STALL_REG, RTC_CNTL_SW_STALL_APPCPU_C1_M);
        CLEAR_PERI_REG_MASK(RTC_CNTL_OPTIONS0_REG, RTC_CNTL_SW_STALL_APPCPU_C0_M);
    }
    else {
        CLEAR_PERI_REG_MASK(RTC_CNTL_SW_CPU_STALL_REG, RTC_CNTL_SW_STALL_PROCPU_C1_M);
        CLEAR_PERI_REG_MASK(RTC_CNTL_OPTIONS0_REG, RTC_CNTL_SW_STALL_PROCPU_C0_M);
    }
}


void ResetChip()
{
    REG_WRITE(RTC_CNTL_OPTIONS0_REG, RTC_CNTL_SW_SYS_RST);
/*
    const uint32_t core_id = xPortGetCoreID();
    // Reset CPUs
    if (core_id == 0) {
        // Running on PRO CPU: APP CPU is stalled. Can reset both CPUs.
        SET_PERI_REG_MASK(RTC_CNTL_OPTIONS0_REG,
            RTC_CNTL_SW_PROCPU_RST_M | RTC_CNTL_SW_APPCPU_RST_M);
    }
    else {
        // Running on APP CPU: need to reset PRO CPU and unstall it,
        // then stall APP CPU
        SET_PERI_REG_MASK(RTC_CNTL_OPTIONS0_REG, RTC_CNTL_SW_PROCPU_RST_M);
        esp_cpu_unstall(0);
        esp_cpu_stall(1);
    }*/
    for (;;)
        ;
}

extern "C" void rtc_printf()
{
}

extern "C" void FLASHHelperEntry(int command, int Arg1, int Arg2)
{
    int Result = -1;
    int i;
    
    switch (command)
    {
        case Initialize:
            //Initialization sequence taken from call_start_cpu0 in bootloader_start.c
            Cache_Read_Disable(0);
            Cache_Read_Disable(1);
            Cache_Flush(0);
            Cache_Flush(1);
            mmu_init(0);
            REG_SET_BIT(DPORT_APP_CACHE_CTRL1_REG, DPORT_APP_CACHE_MMU_IA_CLR);
            mmu_init(1);
            REG_CLR_BIT(DPORT_APP_CACHE_CTRL1_REG, DPORT_APP_CACHE_MMU_IA_CLR);
            REG_CLR_BIT(DPORT_PRO_CACHE_CTRL1_REG, DPORT_PRO_CACHE_MASK_DROM0);
            REG_CLR_BIT(DPORT_APP_CACHE_CTRL1_REG, DPORT_APP_CACHE_MASK_DROM0);
        
            ResetPeripherals();
            spi_flash_attach(0, false);
            Result = 0;
            s_SectorSize = Arg1;
            break;
        case Erase:
            Result = SPIUnlock();
            if (Result == SPI_FLASH_RESULT_OK)
                Result = SPIEraseArea(Arg1, Arg2);
            break;
        case Program:
            for (i = 0; i < Arg2; i += s_SectorSize)
            {
                int todo = Arg2 - i;
                if (todo > s_SectorSize)
                    todo = s_SectorSize;
					
                Result = SPIWrite(Arg1 + i, (uint32_t *)(s_DataBuffer + i), todo);
                if (Result)
                    break;
            }
            break;
        case Reset:
            ResetChip();
            break;
        default:
            Result = -2;
            break;
    }
	
    register unsigned A0 asm("a0");
    A0 = Result;
    asm("break 1, 1");
}