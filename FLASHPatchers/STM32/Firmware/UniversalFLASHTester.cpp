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
	TestedSector Sectors[512];
};


static inline uint32_t WordFromAddr(uint32_t addr)
{
	return (addr * 81247345);
}

static volatile FLASHTesterConfiguration g_FLASHTesterConfiguration;

extern "C" int  __attribute__((noinline, noclone, externally_visible)) RunFLASHTest()
{
	FLASHTesterConfiguration *cfg = (FLASHTesterConfiguration *)&g_FLASHTesterConfiguration;
	TestedSector *sectors = cfg->Sectors;
	cfg->FLASHPatcher_Init();
	
	for (int i = 0; i < cfg->SectorCount; i++)
	{
		if (cfg->FLASHPatcher_EraseSectors(sectors[i].Bank, sectors[i].ID, 1))
			return -7;
	}
	
	for (uint32_t addr = cfg->GlobalStart; addr < cfg->GlobalEnd; addr += 16)
	{
		uint32_t words[4] = { 0, 0, 0, 0 };
		if (cfg->FLASHPatcher_ProgramQWord((void *)addr, words))
			return -1;
	}
	
	for (uint32_t addr = cfg->GlobalStart; addr < cfg->GlobalEnd; addr += 4)
	{
		if (*((uint32_t *)addr) != 0)
			return -2;
	}

	for (int i = 0; i < cfg->SectorCount; i++)
	{
		cfg->FLASHPatcher_Init();
		
		if (cfg->FLASHPatcher_EraseSectors(sectors[i].Bank, sectors[i].ID, 1))
			return -3;
		
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
				return -4;
		}
		
		cfg->FLASHPatcher_Init();
		
		for (uint32_t addr = sectors[i].Start; addr < (sectors[i].Start + sectors[i].Size); addr += 16)
		{
			uint32_t words[4];
			for (int j = 0; j < 4; j++)
				words[j] = WordFromAddr(addr + j * 4);
			
			if (cfg->FLASHPatcher_ProgramQWord((void *)addr, words))
				return -5;
		}
		
		cfg->FLASHPatcher_Complete();
		for (uint32_t addr = cfg->GlobalStart; addr < cfg->GlobalEnd; addr += 4)
		{
			uint32_t word = *((uint32_t *)addr);
			uint32_t expected;
			if (addr < (sectors[i].Start + sectors[i].Size))
				expected = WordFromAddr(addr);
			else
				expected = 0;
			
			if (word != expected)
				return -6;
		}
	}

	return 0;
}

extern "C" int Reset_Handler()
{
	return RunFLASHTest();
}
