#include "FLASHPatcherAPI.h"

enum FLASHPatcherCommand
{
	fpcEraseSector = 0xA0, //<sector>, <count>
	fpcProgramWords, //<address>, <count>, <data>
	fpcFlushCache,
	fpcEnd,
};

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
		static uint32_t words[8];
		FLASHPatcher_Init();
		for (;;)
		{
			uint8_t byte = ReadByteBlocking(offset, BufferSize);
			int st;
			switch (byte)
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
					if (burstSize > (sizeof(words) / sizeof(words[0])))
						return Status = 1003;
					for (int i = 0; i < totalSize; i += burstSize)
					{
						for (int j = 0; j < burstSize; j++)
							words[j] = ReadWordBlocking(offset, BufferSize);
					
						st = FLASHPatcher_ProgramWords(bank, (void *)(address + i * 4), words, burstSize);
						if (st)
							return Status = st;
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

extern "C" uint32_t HAL_GetTick(void)
{
	return 0;
}

CircularBuffer *g_pBuffer;

extern "C" int FLASHPatcher_RunRequestLoop(CircularBuffer *buffer)
{
	return buffer->RunRequestLoop();
}
