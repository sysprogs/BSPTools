#include <sys/types.h>

struct TestedSector
{
	uint32_t Bank;
	uint32_t ID;	
	uint32_t Start;
	uint32_t Size;
};

struct FLASHTesterConfiguration
{
	int(*FLASHPatcher_Init)();
	int(*FLASHPatcher_EraseSectors)(int bank, int firstSector, int count);
	int(*FLASHPatcher_ProgramQWord)(void *address, uint32_t *words);
	int(*FLASHPatcher_Complete)();
	int SectorCount;
	uint32_t GlobalStart, GlobalEnd;
	TestedSector Sectors[4096];
};


static inline uint32_t WordFromAddr(uint32_t addr)
{
	return (addr * 81247345);
}

static volatile struct
{
	int Phase, SubPhase;
	uint32_t Address, Sector;
} g_ObservableState;


void Error_Handler()
{
	asm("bkpt #0");
}

extern "C" void Reset_Handler();


extern volatile FLASHTesterConfiguration _EndOfStackStartOfConfigTable;
void * g_FLASHPatcherTesterVectors[0x30] __attribute__((section(".isr_vector"), used)) = 
{
	(void *)&_EndOfStackStartOfConfigTable,
	(void *)&Reset_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
	(void *)&Error_Handler,
};

static int WrapError(int err)
{
	asm("bkpt #255");
	return err;
}

#ifdef DEBUG
#define UNEXPECTED(x) WrapError(x)
#else
#define UNEXPECTED(x) (x)
#endif

extern "C" int  __attribute__((noinline, noclone, externally_visible)) RunFLASHTest()
{
	//*((void **)0xE000ED08) = g_FLASHPatcherTesterVectors;
	
	FLASHTesterConfiguration *cfg = (FLASHTesterConfiguration *)&_EndOfStackStartOfConfigTable;
	TestedSector *sectors = cfg->Sectors;
	cfg->FLASHPatcher_Init();
	g_ObservableState.Phase = 10;
	g_ObservableState.SubPhase = 0;
	g_ObservableState.Address = 0;
	g_ObservableState.Sector = 0;
	
	for (int i = 0; i < cfg->SectorCount; i++)
	{
		if (cfg->FLASHPatcher_EraseSectors(sectors[i].Bank, sectors[i].ID, 1))
			return UNEXPECTED(-g_ObservableState.Phase);
		g_ObservableState.Sector = i;
	}

	g_ObservableState.Phase += 10;
	
	for (uint32_t addr = cfg->GlobalStart; addr < cfg->GlobalEnd; addr += 16)
	{
		uint32_t words[4] = { 0, 0, 0, 0 };
		if (cfg->FLASHPatcher_ProgramQWord((void *)addr, words))
			return UNEXPECTED(-g_ObservableState.Phase);
		g_ObservableState.Address = addr;
	}
	
	cfg->FLASHPatcher_Complete();
	g_ObservableState.Phase += 10;
	
	for (uint32_t addr = cfg->GlobalStart; addr < cfg->GlobalEnd; addr += 4)
	{
		if (*((uint32_t *)addr) != 0)
			return UNEXPECTED(-g_ObservableState.Phase);
	}

	g_ObservableState.Phase += 10;
	
	for (int i = 0; i < cfg->SectorCount; i++)
	{
		g_ObservableState.Sector = i;
		g_ObservableState.SubPhase = 0;
		
		cfg->FLASHPatcher_Init();
		
		if (cfg->FLASHPatcher_EraseSectors(sectors[i].Bank, sectors[i].ID, 1))
			return UNEXPECTED(-g_ObservableState.Phase - g_ObservableState.SubPhase);
		
		g_ObservableState.SubPhase++;
		cfg->FLASHPatcher_Complete();
		
		for (uint32_t addr = sectors[i].Start; addr < (sectors[i].Start + sectors[i].Size); addr += 4)
		{
			uint32_t word = *((uint32_t *)addr);
			uint32_t expected;
			if (addr < sectors[i].Start)
				expected = WordFromAddr(addr);
			else if (addr < (sectors[i].Start + sectors[i].Size))
				expected = -1;
			else
				expected = 0;
			
			if (word != expected)
				return UNEXPECTED(-g_ObservableState.Phase - g_ObservableState.SubPhase);
		}
		
		g_ObservableState.SubPhase++;
		cfg->FLASHPatcher_Init();
		
		for (uint32_t addr = sectors[i].Start; addr < (sectors[i].Start + sectors[i].Size); addr += 16)
		{
			uint32_t words[4];
			for (int j = 0; j < 4; j++)
				words[j] = WordFromAddr(addr + j * 4);
			
			if (cfg->FLASHPatcher_ProgramQWord((void *)addr, words))
				return UNEXPECTED(-g_ObservableState.Phase - g_ObservableState.SubPhase);
			
			g_ObservableState.Address = addr;
		}
		
		cfg->FLASHPatcher_Complete();
		g_ObservableState.SubPhase++;
		for (uint32_t addr = cfg->GlobalStart; addr < sectors[i].Start; addr += 1024)
		{
			uint32_t word = *((uint32_t *)addr);
			uint32_t expected = WordFromAddr(addr);
			
			if (word != expected)
				return UNEXPECTED(-g_ObservableState.Phase - g_ObservableState.SubPhase);
			
			g_ObservableState.Address = addr;
		}
		
		g_ObservableState.SubPhase++;
		for (uint32_t addr = sectors[i].Start; addr < (sectors[i].Start + sectors[i].Size); addr += 4)
		{
			uint32_t word = *((uint32_t *)addr);
			uint32_t expected = WordFromAddr(addr);
			
			if (word != expected)
				return UNEXPECTED(-g_ObservableState.Phase - g_ObservableState.SubPhase);
			
			g_ObservableState.Address = addr;
		}
		
		g_ObservableState.SubPhase++;
		for (uint32_t addr = (sectors[i].Start + sectors[i].Size); addr < cfg->GlobalEnd; addr += 1024)
		{
			uint32_t word = *((uint32_t *)addr);
			uint32_t expected = 0;
			
			if (word != expected)
				return UNEXPECTED(-g_ObservableState.Phase - g_ObservableState.SubPhase);
			
			g_ObservableState.Address = addr;
		}
	}
	
	g_ObservableState.Phase += 10;
	return 0;
}

volatile int g_FinalResult;
extern "C" void __attribute__((naked)) Reset_Handler()
{
	asm("ldr r0, =_EndOfStackStartOfConfigTable");
	asm("mov sp, r0");
	asm("bkpt 0");
	g_FinalResult = RunFLASHTest();
	for (;;)
		asm("bkpt 255");
}
