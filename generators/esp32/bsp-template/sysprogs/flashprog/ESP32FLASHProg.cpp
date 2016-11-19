#include "spi_flash.h"

enum FLASHCommandType
{
    Initialize,
    Erase,
    Program,
};

struct Header
{
    int Signature;
    void *LoadAddress;
    void *EntryPoint;
    void *DataBuffer;
    unsigned DataBufferSize;
    void *Stack;
    unsigned StackSize;
};

static int s_SectorSize;

extern "C" void FLASHHelperEntry(int command, int Arg1, int Arg2);

char s_DataBuffer[65536];
char s_Stack[1024];

Header s_Header __attribute__((section(".headers"))) = { 
    '23LF',
    &s_Header,
    (void *)&FLASHHelperEntry,
    s_DataBuffer,
    sizeof(s_DataBuffer),
    s_Stack,
    sizeof(s_Stack)
};

extern "C" 
{
    void Cache_Read_Disable(int);
    void Cache_Flush(int);
}

extern "C" void FLASHHelperEntry(int command, int Arg1, int Arg2)
{
    int Result = -1;
    int i;
    
    switch (command)
    {
        case Initialize:
            s_SectorSize = Arg1;
            Cache_Read_Disable(0);
            Cache_Flush(0);
            spi_flash_attach(0, false);
            Result = 0;
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
        default:
            Result = -2;
            break;
    }
	
    register unsigned A0 asm("a0");
    A0 = Result;
    asm("break 1, 1");
}