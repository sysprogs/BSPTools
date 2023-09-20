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
	int(*FLASHPatcher_ProgramWords)(int bank, void *address, const uint32_t *words, int wordCount);
	int(*FLASHPatcher_Complete)();
	int SectorCount;
	uint32_t GlobalStart, GlobalEnd;
	uint32_t ErasedValue;
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

static const int ProgramBurstSize = 8;

extern "C" int  __attribute__((noinline, noclone, externally_visible)) RunFLASHTest()
{
	static const uint32_t ProgrammedFillerValue = 0x55555555;
	
	//*((void **)0xE000ED08) = g_FLASHPatcherTesterVectors;
	
	FLASHTesterConfiguration *cfg = (FLASHTesterConfiguration *)&_EndOfStackStartOfConfigTable;
	cfg->FLASHPatcher_Init();
	g_ObservableState.Phase = 10;
	g_ObservableState.SubPhase = 0;
	g_ObservableState.Address = 0;
	g_ObservableState.Sector = 0;
	
	for (int i = 0; i < cfg->SectorCount; i++)
	{
		if (cfg->FLASHPatcher_EraseSectors(cfg->Sectors[i].Bank, cfg->Sectors[i].ID, 1))
			return UNEXPECTED(-g_ObservableState.Phase);
		g_ObservableState.Sector = i;
	}

	g_ObservableState.Phase += 10;
	static uint32_t words[ProgramBurstSize];
	for (int i = 0; i < ProgramBurstSize; i++)
		words[i] = ProgrammedFillerValue;
	
	for (int i = 0; i < cfg->SectorCount; i++)
	{
		TestedSector *sector = &cfg->Sectors[i];
		
		for (uint32_t addr = sector->Start; addr < (sector->Start + sector->Size); addr += sizeof(words))
		{
			if (cfg->FLASHPatcher_ProgramWords(sector->Bank, (void *)addr, words, sizeof(words) / sizeof(words[0])))
				return UNEXPECTED(-g_ObservableState.Phase);
			
			g_ObservableState.Address = addr;
		}
		
		g_ObservableState.Sector = i;
	}

	
	cfg->FLASHPatcher_Complete();
	g_ObservableState.Phase += 10;
	
	for (uint32_t addr = cfg->GlobalStart; addr < cfg->GlobalEnd; addr += 4)
	{
		if (*((uint32_t *)addr) != ProgrammedFillerValue)
			return UNEXPECTED(-g_ObservableState.Phase);
	}

	g_ObservableState.Phase += 10;
	
	for (int i = 0; i < cfg->SectorCount; i++)
	{
		TestedSector *sector = &cfg->Sectors[i];
		
		g_ObservableState.Sector = i;
		g_ObservableState.SubPhase = 0;
		
		cfg->FLASHPatcher_Init();
		
		if (cfg->FLASHPatcher_EraseSectors(sector->Bank, sector->ID, 1))
			return UNEXPECTED(-g_ObservableState.Phase - g_ObservableState.SubPhase);
		
		g_ObservableState.SubPhase++;
		cfg->FLASHPatcher_Complete();
		
		for (uint32_t addr = sector->Start; addr < (sector->Start + sector->Size); addr += 4)
		{
			uint32_t word = *((uint32_t *)addr);
			uint32_t expected;
			if (addr < sector->Start)
				expected = WordFromAddr(addr);
			else if (addr < (sector->Start + sector->Size))
				expected = cfg->ErasedValue;
			else
				expected = ProgrammedFillerValue;
			
			if (word != expected)
				return UNEXPECTED(-g_ObservableState.Phase - g_ObservableState.SubPhase);
		}
		
		g_ObservableState.SubPhase++;
		cfg->FLASHPatcher_Init();
		
		for (uint32_t addr = sector->Start; addr < (sector->Start + sector->Size); addr += sizeof(words))
		{
			for (int j = 0; j < ProgramBurstSize; j++)
				words[j] = WordFromAddr(addr + j * 4);
			
			if (cfg->FLASHPatcher_ProgramWords(sector->Bank, (void *)addr, words, ProgramBurstSize))
				return UNEXPECTED(-g_ObservableState.Phase - g_ObservableState.SubPhase);
			
			g_ObservableState.Address = addr;
		}
		
		cfg->FLASHPatcher_Complete();
		g_ObservableState.SubPhase++;
		for (uint32_t addr = cfg->GlobalStart; addr < sector->Start; addr += 1024)
		{
			uint32_t word = *((uint32_t *)addr);
			uint32_t expected = WordFromAddr(addr);
			
			if (word != expected)
				return UNEXPECTED(-g_ObservableState.Phase - g_ObservableState.SubPhase);
			
			g_ObservableState.Address = addr;
		}
		
		g_ObservableState.SubPhase++;
		for (uint32_t addr = sector->Start; addr < (sector->Start + sector->Size); addr += 4)
		{
			uint32_t word = *((uint32_t *)addr);
			uint32_t expected = WordFromAddr(addr);
			
			if (word != expected)
				return UNEXPECTED(-g_ObservableState.Phase - g_ObservableState.SubPhase);
			
			g_ObservableState.Address = addr;
		}
		
		g_ObservableState.SubPhase++;
		for (uint32_t addr = (sector->Start + sector->Size); addr < cfg->GlobalEnd; addr += 1024)
		{
			uint32_t word = *((uint32_t *)addr);
			
			if (word != ProgrammedFillerValue)
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
