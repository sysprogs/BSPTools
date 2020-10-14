#include <TinyEmbeddedTest.h>

#ifdef __ICCARM__
#include <iar_dlmalloc.h>
#else
#include <malloc.h>
#endif

class MemoryLeakTestScope
{
  private:
	const char *m_pName;
	size_t m_InitialUsedSpace;

	static size_t GetNumberOfAllocatedBytes()
	{
#if defined (SIMULATION) && defined(_WIN32)
		extern size_t GetNumberOfWin32AllocatedBytes();
		return GetNumberOfWin32AllocatedBytes();
#else
		struct mallinfo info;
#ifdef __ICCARM__
		info = __iar_dlmallinfo();
#else
		info = mallinfo();
#endif
		return info.uordblks;
#endif
	}

  public:
	MemoryLeakTestScope(const char *pName = nullptr)
		: m_pName(pName)
	{
		m_InitialUsedSpace = GetNumberOfAllocatedBytes();
	}

	MemoryLeakTestScope(const MemoryLeakTestScope &) = delete;

	~MemoryLeakTestScope()
	{
		size_t usedSpace = GetNumberOfAllocatedBytes();
		if (usedSpace != m_InitialUsedSpace)
		{
			if (m_pName)
				ReportTestFailure("%s - %d heap bytes leaked", m_pName, (int)(usedSpace - m_InitialUsedSpace));
			else
				ReportTestFailure("%d heap bytes leaked", m_pName, (usedSpace - m_InitialUsedSpace));
		}
	}
};

/*
	Usage:

	TEST(MyGroup, MyTest)
	{
		CHECK_FOR_MEMORY_LEAKS();

		//Call functions that should not leak any memory
	}

	TEST(MyGroup, Test2)
	{
		SomeInitialization();
		
		{
			CHECK_FOR_MEMORY_LEAKS();	//Nested leak check scope
			
			//Call functions that should not leak any memory
		}

	}

	You can also just instantiate the memory leak checker explicitly:

	{
		MemoryLeakTestScope scope;
		//Call functions that should not leak any memory
	}
 */

#define CHECK_FOR_MEMORY_LEAKS() MemoryLeakTestScope memoryLeakTestScope##__COUNTER__
#define CHECK_FOR_MEMORY_LEAKS_NAMED(name) MemoryLeakTestScope memoryLeakTestScope##__COUNTER__(name)
