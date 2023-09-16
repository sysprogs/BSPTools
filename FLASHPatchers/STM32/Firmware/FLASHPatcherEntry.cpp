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
		FLASHPatcher_Init();
		for (;;)
		{
			uint8_t byte = ReadByteBlocking(offset, BufferSize);
			uint32_t arg1, arg2, arg3;
			int st;
			switch (byte)
			{
			case fpcEraseSector:
				arg1 = ReadWordBlocking(offset, BufferSize);
				arg2 = ReadWordBlocking(offset, BufferSize);
				arg3 = ReadWordBlocking(offset, BufferSize);
			
				st = FLASHPatcher_EraseSectors(arg1, arg2, arg3);
				if (st != 0)
					return Status = st;
				break;
			case fpcProgramWords:
				arg1 = ReadWordBlocking(offset, BufferSize);
				arg2 = ReadWordBlocking(offset, BufferSize);
				if (arg2 & 3)
					return 1003;
				for (int i = 0; i < arg2; i+=4)
				{
					uint32_t words[4];
					for (int j = 0; j < 4; j++)
						words[j] = ReadWordBlocking(offset, BufferSize);
					
					st = FLASHPatcher_ProgramQWord((void *)(arg1 + i * 4), words);
					if (st)
						return Status = st;
				}
				break;
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

extern "C" int __attribute__((noinline, noclone, externally_visible)) FLASHPatcher_ProgramBuffer(void *addr, int wordCount, const uint32_t *buffer)
{
	for (int i = 0; i < wordCount; i += 4)
	{
		int st = FLASHPatcher_ProgramQWord((uint32_t *)addr + i, buffer + i);
		if (st)
			return st;
	}
	
	return 0;
}

extern "C" uint32_t HAL_GetTick(void)
{
	return 0;
}

CircularBuffer *g_pBuffer;

extern "C" int FLASHPatcher_RunRequestLoop(CircularBuffer *buffer)
{
	return buffer->RunRequestLoop();
}
