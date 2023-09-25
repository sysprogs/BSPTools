#include "FLASHPatcherAPI.h"

enum FLASHPatcherCommand
{
	fpcEraseSector = 0xA0, //<sector>, <count>
	fpcProgramWords, //<bank>, <address>, <burst size>, <normal size>, <tail repeat size>, <data>
	fpcFlushCache,
	fpcEnd,
};

static uint32_t FLASHPatcher_BurstBuffer[FLASHPatcher_ProgramBurstSizeInWords];

class CircularBuffer
{
public:
	volatile uint32_t Status;
	volatile uint32_t RequestsProcessed;
	
private:
	volatile uint32_t Rd, Wr;
	uint32_t BufferSize;
	volatile uint8_t Data[0];
	
private:
	inline uint8_t ReadByteBlocking(uint32_t &offset, uint32_t bufferSize)
	{
		while (Rd == Wr)
		{
		}
	
		Rd++;
		if (offset >= bufferSize)
			offset = 0;
	
		return Data[offset++];
	}

	inline uint32_t ReadWordBlocking(uint32_t &offset, uint32_t bufferSize)
	{
		uint32_t result = 0;
		for (int i = 0; i < 4; i++)
		{
			result >>= 8;
			result |= (ReadByteBlocking(offset, bufferSize) << 24);
		}
		return result;
	}
	
public:
	int RunRequestLoop()
	{
		uint32_t offset = 0;
		FLASHPatcher_Init();
		for (;;)
		{
			uint8_t cmd = ReadByteBlocking(offset, BufferSize);
			int st;
			switch (cmd)
			{
			case fpcEraseSector:
				{
					uint32_t bank = ReadWordBlocking(offset, BufferSize);
					uint32_t firstSector = ReadWordBlocking(offset, BufferSize);
					uint32_t count = ReadWordBlocking(offset, BufferSize);
			
					st = FLASHPatcher_EraseSectors(bank, firstSector, count);
					if (st != 0)
						return Status = st;
					break;
				}
			case fpcProgramWords:
				{
					uint32_t bank = ReadWordBlocking(offset, BufferSize);
					uint32_t address = ReadWordBlocking(offset, BufferSize);
					uint32_t burstSize = ReadWordBlocking(offset, BufferSize);
					uint32_t totalSize = ReadWordBlocking(offset, BufferSize);
					uint32_t tailSize = ReadWordBlocking(offset, BufferSize);
					if (burstSize > (sizeof(FLASHPatcher_BurstBuffer) / sizeof(FLASHPatcher_BurstBuffer[0])))
						return Status = 1003;
					for (int i = 0; i < totalSize; i += burstSize)
					{
						for (int j = 0; j < burstSize; j++)
							FLASHPatcher_BurstBuffer[j] = ReadWordBlocking(offset, BufferSize);
						
						st = FLASHPatcher_ProgramWords(bank, (void *)address, FLASHPatcher_BurstBuffer, burstSize);
						if (st)
							return Status = st;
						
						address += burstSize * 4;
					}
					
					for (int i = 0; i < tailSize; i += burstSize)
					{
						st = FLASHPatcher_ProgramWords(bank, (void *)address, FLASHPatcher_BurstBuffer, burstSize);
						if (st)
							return Status = st;
						
						address += burstSize * 4;
					}
					break;
				}
			case fpcEnd:
				st = FLASHPatcher_Complete();
				if (st)
					return Status = st;
					
				RequestsProcessed++;
				return Status = 0;
			default:
				return Status = -2;
			}
		
			RequestsProcessed++;
		}
	}
};

int __attribute__((noinline, noclone)) FLASHPatcher_ProgramRepeatedWords(int bank, void *address, const uint32_t *words, int wordCount, int totalWordCount)
{
	int i = 0;
	for (i = 0; i < totalWordCount; i += wordCount)
	{
		int st = FLASHPatcher_ProgramWords(bank, (char *)address + i * 4, words, wordCount);
		if (st)
			return st;
	}
	
	if (i != totalWordCount)
		return -1005;
	
	return 0;
}

CircularBuffer *g_pBuffer;

extern "C" int FLASHPatcher_RunRequestLoop(CircularBuffer *buffer)
{
	return buffer->RunRequestLoop();
}
