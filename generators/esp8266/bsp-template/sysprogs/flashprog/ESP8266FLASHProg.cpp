#ifdef __cplusplus
extern "C"
{
#endif
	
#include <c_types.h>
#include <eagle_soc.h>
#include <gpio.h>
	
#ifdef __cplusplus
}
#endif

enum FLASHCommandType
{
	Initialize,
	Erase,
	Program,
};

struct ParameterArea
{
	int Command;
	unsigned Arg1;
	unsigned Arg2;
	int Result;
} s_ParameterArea;

struct Header
{
	int Signature;
	void *LoadAddress;
	void *EntryPoint;
	void *ParameterArea;
	void *DataBuffer;
	unsigned DataBufferSize;
};

static int s_SectorSize = 4096, s_EraseBlockSize = 4096;

extern "C" void FLASHHelperEntry();

char s_DataBuffer[65536];

Header s_Header __attribute__((section(".headers"))) = { 
	'HSLF',
	&s_Header,
	(void *)&FLASHHelperEntry,
	&s_ParameterArea,
	s_DataBuffer,
	sizeof(s_DataBuffer)
};

extern "C" 
{
	int spi_flash_attach();
	void Cache_Read_Enable(int, int, int);
	void Cache_Read_Disable(void);
	int SPIWrite(unsigned dst, const void *src, unsigned size);
	int SPIEraseSector(uint16_t sector);
}

void FLASHHelperEntry()
{
	int i;
	switch (s_ParameterArea.Command)
	{
		case Initialize:
			if (s_ParameterArea.Arg1)
				s_SectorSize = s_ParameterArea.Arg1;
			if (s_ParameterArea.Arg2)
			s_EraseBlockSize = s_ParameterArea.Arg2;
			s_ParameterArea.Result = spi_flash_attach();
			break;
		case Erase:
			Cache_Read_Disable();
			for (i = 0; i < s_ParameterArea.Arg2; i += s_EraseBlockSize)
			{
				s_ParameterArea.Result = SPIEraseSector((s_ParameterArea.Arg1 + i) / s_EraseBlockSize);
				if (s_ParameterArea.Result)
					break;
			}
			Cache_Read_Enable(0, 0, 1);
			break;
		case Program:
			Cache_Read_Disable();
			for (i = 0; i < s_ParameterArea.Arg2; i += s_SectorSize)
			{
				int todo = s_ParameterArea.Arg2 - i;
				if (todo > s_SectorSize)
					todo = s_SectorSize;
					
				s_ParameterArea.Result = SPIWrite(s_ParameterArea.Arg1 + i, s_DataBuffer + i, todo);
				if (s_ParameterArea.Result)
					break;
			}
			Cache_Read_Enable(0, 0, 1);
			break;
		default:
			s_ParameterArea.Result = -2;
			break;
	}
	
	asm("break 1, 1");
}